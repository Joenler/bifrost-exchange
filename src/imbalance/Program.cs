using System.Globalization;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Imbalance;
using Bifrost.Imbalance.HostedServices;
using Bifrost.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

var builder = Host.CreateApplicationBuilder(args);

// Overlay config/hackathon.json on top of appsettings.json so the
// ImbalanceSimulator section (reference defaults for K, alpha, regime gammas,
// S_q per quarter, etc.) can live in the shared tournament config. Falls back
// silently if the file is absent — defaults in ImbalanceSimulatorOptions then
// carry the values.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "config", "hackathon.json"),
    optional: true,
    reloadOnChange: false);

// Source-layout fallback for `dotnet run` from the repo root: the tournament
// config lives at bifrost-exchange/config/hackathon.json relative to the
// service's obj/ path.
var repoConfig = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "hackathon.json");
if (File.Exists(repoConfig))
{
    builder.Configuration.AddJsonFile(repoConfig, optional: true, reloadOnChange: false);
}

builder.Services.Configure<ImbalanceSimulatorOptions>(
    builder.Configuration.GetSection("ImbalanceSimulator"));

// ---------- RabbitMQ connection (Polly-retried via shared resilience pipeline) ----------
// Mirrors the exchange + quoter Program.cs pattern; the resilience pipeline
// tolerates the cold-boot race where the imbalance container starts polling
// before the broker's AMQP listener is ready.
var rabbitConfig = builder.Configuration.GetSection("RabbitMq");
var factory = new ConnectionFactory
{
    HostName = rabbitConfig["Host"] ?? "rabbitmq",
    Port = int.Parse(rabbitConfig["Port"] ?? "5672", CultureInfo.InvariantCulture),
    UserName = rabbitConfig["Username"] ?? "guest",
    Password = rabbitConfig["Password"] ?? "guest",
};

using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("Bifrost.Imbalance.Startup");
var pipeline = RabbitMqResilience.CreateConnectionPipeline(startupLogger);

var connection = await pipeline.ExecuteAsync(
    async ct => await factory.CreateConnectionAsync("bifrost-imbalance", ct),
    CancellationToken.None);
builder.Services.AddSingleton<IConnection>(connection);

// ---------- Clock + RoundState seams ----------
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);

// Production static round-state source: the initial state is read from
// appsettings.json (RoundState:Initial). A future RabbitMQ-backed source will
// swap in here once the orchestrator publishes round transitions. Tests
// substitute MockRoundStateSource via the test host.
builder.Services.AddSingleton<IRoundStateSource>(_ =>
    new ConfigRoundStateSource(
        Enum.Parse<RoundState>(builder.Configuration["RoundState:Initial"] ?? "IterationOpen")));

// ---------- Pure-math services ----------
builder.Services.AddSingleton<QuarterIndexResolver>();
builder.Services.AddSingleton<IRandomSource>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ImbalanceSimulatorOptions>>();
    return new SeededRandomSource(opts.Value.ScenarioSeed);
});
builder.Services.AddSingleton<SimulatorState>();
builder.Services.AddSingleton<ImbalancePricingEngine>();

// ---------- Single shared Channel<SimulatorMessage> ----------
// Bounded at 8192 (matches BufferedEventPublisher capacity). FullMode=Wait is
// LOAD-BEARING: dropping a fill would silently corrupt (clientId, QH) →
// net_position, and the settlement row computed at Gate would be wrong. We
// accept producer back-pressure rather than silent loss.
builder.Services.AddSingleton(_ =>
    Channel.CreateBounded<SimulatorMessage>(new BoundedChannelOptions(8192)
    {
        SingleReader = true,
        SingleWriter = false,        // multiple producer hosted services enqueue
        FullMode = BoundedChannelFullMode.Wait,
    }));

// ---------- Dedicated AMQP IChannel per publisher ----------
// RabbitMQ.Client 7.x channels are NOT thread-safe. The buffered publisher's
// drain task runs concurrently with every emission path, so it owns its own
// AMQP channel. The sync GetAwaiter().GetResult() runs exactly once at DI
// build time — no loop hot-path exposure.
builder.Services.AddSingleton<RabbitMqEventPublisher>(sp =>
{
    var conn = sp.GetRequiredService<IConnection>();
    var amqpChannel = conn.CreateChannelAsync().GetAwaiter().GetResult();
    var clock = sp.GetRequiredService<IClock>();
    return new RabbitMqEventPublisher(amqpChannel, clock);
});
builder.Services.AddSingleton<BufferedEventPublisher>(sp =>
    new BufferedEventPublisher(
        sp.GetRequiredService<RabbitMqEventPublisher>(),
        sp.GetService<ILogger<BufferedEventPublisher>>()));
builder.Services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<BufferedEventPublisher>());

// ---------- Hosted services ----------
// StartupLogger writes /tmp/bifrost-ready so docker compose can mark this
// service healthy via the sentinel HEALTHCHECK.
builder.Services.AddHostedService<StartupLogger>();

// Fill consumer: subscribes to private.exec.*.fill on the private topic
// exchange, resolves quarter_index via QuarterIndexResolver (drops the hour
// instrument at the boundary), and enqueues FillMessage onto the shared
// channel for the actor loop to accumulate (clientId, QH) -> net_position.
builder.Services.AddHostedService<FillConsumerHostedService>();

// Shock consumer: subscribes to events.physical.shock on the public topic
// exchange, defensively range-checks the per-quarter index (drops out-of-range
// shocks with an Error log so an upstream contract regression surfaces
// loudly), and enqueues ShockMessage onto the shared channel for A_physical
// accumulation. The future orchestrator phase owns the publisher — until then
// the binding is a no-op in production.
builder.Services.AddHostedService<ShockConsumerHostedService>();

// Forecast timer: PeriodicTimer wired through the injected TimeProvider
// fires every TForecastSeconds and enqueues a ForecastTickMessage. The drain
// loop's HandleForecastTick arm gates emission on CurrentRoundState ==
// RoundOpen and publishes a single scalar ForecastUpdateEvent averaged across
// the four QHs via PublishPublicEvent on public.forecast.
builder.Services.AddHostedService<ForecastTimerHostedService>();

// Round-state bridge wires onto the shared channel in a later pass.
builder.Services.AddHostedService<SimulatorActorLoop>();

var host = builder.Build();
await host.RunAsync();
