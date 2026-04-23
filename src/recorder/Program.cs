using System.Reflection;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Recorder;
using Bifrost.Recorder.Infrastructure;
using Bifrost.Recorder.Session;
using Bifrost.Recorder.Storage;
using Bifrost.Time;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

var builder = Host.CreateApplicationBuilder(args);

// Clock first so every singleton that needs it shares one instance.
IClock clock = new SystemClock();
builder.Services.AddSingleton(clock);

// Dapper TypeHandlers BEFORE any query executes (Pitfall 6). Registration is
// process-global; safe to call once at startup.
SqlMapper.AddTypeHandler(new DecimalTypeHandler());
SqlMapper.AddTypeHandler(new BoolTypeHandler());

var sessionsRoot = builder.Configuration["Recorder:SessionsRoot"] ?? "/data/sessions";
Directory.CreateDirectory(sessionsRoot);

using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());

var sessionManager = new SessionManager(clock, startupLoggerFactory.CreateLogger<SessionManager>());
var runId = sessionManager.GenerateRunId();
Console.WriteLine($"Recorder session: {runId}");

var sessionDir = sessionManager.CreateSessionDirectory(sessionsRoot, runId);
var dbPath = SessionManager.GetDbPath(sessionDir);

// Open DB + apply WAL pragmas + run migrations BEFORE AddHostedService<WriteLoop>()
// (Pitfall 7). Migrations are synchronous and fast (file create + 5 pragmas +
// 8 CREATE TABLE on cold start).
var db = new SessionDatabase($"Data Source={dbPath}");
db.InitializePragmas();

var migrator = new SchemaMigrator(db, clock, startupLoggerFactory.CreateLogger<SchemaMigrator>());
migrator.ApplyPending();

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(sessionManager);

var manifest = new Manifest
{
    RunId = runId,
    EventRunId = runId,
    Name = $"Session {runId}",
    StartTime = clock.GetUtcNow(),
    ParticipatingTeams = [],
    ScenarioSeeds = [],
    McOperatorHostname = Environment.MachineName,
    BifrostVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0",
};

// Preliminary manifest: null ExitReason signals crash if process dies before shutdown.
sessionManager.WriteManifest(sessionDir, manifest);
builder.Services.AddSingleton(manifest);

// Bounded channel; DropOldest is the back-pressure contract per RESEARCH.md.
var writeChannel = Channel.CreateBounded<WriteCommand>(new BoundedChannelOptions(10_000)
{
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true,
    SingleWriter = false,
});
builder.Services.AddSingleton(writeChannel);
builder.Services.AddSingleton<RecorderMetrics>();

var sessionIndex = new SessionIndex(sessionsRoot);
builder.Services.AddSingleton(sessionIndex);

var exitDetector = new ExitReasonDetector(clock, TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(exitDetector);

// Hosted services: WriteLoop + Consumer + StartupLogger (the Phase 00 sentinel
// that writes /tmp/bifrost-ready for the Docker healthcheck).
builder.Services.AddSingleton(sp => new WriteLoop(
    writeChannel,
    db,
    sp.GetRequiredService<RecorderMetrics>(),
    sp.GetRequiredService<ILogger<WriteLoop>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<WriteLoop>());

builder.Services.AddHostedService(sp => new RabbitMqRecorderConsumer(
    sp.GetRequiredService<IConfiguration>(),
    writeChannel,
    sessionManager,
    sessionIndex,
    exitDetector,
    manifest,
    sessionDir,
    db,
    sp.GetRequiredService<RecorderMetrics>(),
    clock,
    sp.GetRequiredService<ILogger<RabbitMqRecorderConsumer>>()));

builder.Services.AddHostedService<StartupLogger>();

// Recorder survives an unhandled failure in any hosted service (data-loss
// avoidance): the remaining hosted services keep draining, which is strictly
// preferable to a crash that loses the in-flight batch.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

var app = builder.Build();

// Graceful-shutdown hook: checkpoint WAL + stamp the manifest's ExitReason +
// EndTime before the process exits. This runs after StopAsync on each hosted
// service, which ensures the consumer has already drained to the channel and
// the write loop has already drained the channel to disk.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        manifest.EndTime = clock.GetUtcNow();
        manifest.ExitReason = exitDetector.Detect(cancellationRequested: true);
        sessionManager.WriteManifest(sessionDir, manifest);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Graceful shutdown hook failed: {ex}");
    }
});

await app.RunAsync();
