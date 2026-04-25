using System.Globalization;
using Bifrost.Contracts.Internal;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Gateway;
using Bifrost.Gateway.Guards;
using Bifrost.Gateway.MassCancel;
using Bifrost.Gateway.Position;
using Bifrost.Gateway.Rabbit;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Streaming;
using Bifrost.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

// Pitfall 1 (07-RESEARCH.md §Common Pitfalls): Kestrel HTTP/2 KeepAlivePingDelay defaults
// to TimeSpan.MaxValue (DISABLED). GW-07 mass-cancel-on-disconnect SLO depends on these
// pings firing. appsettings.json sets explicit values; this defensive override guards
// against configuration-load failures leaving the framework defaults in place.
builder.WebHost.ConfigureKestrel(o =>
{
    if (o.Limits.Http2.KeepAlivePingDelay == TimeSpan.MaxValue)
        o.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10);
    if (o.Limits.Http2.KeepAlivePingTimeout == TimeSpan.MaxValue)
        o.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 4 * 1024 * 1024;
    options.MaxSendMessageSize = 4 * 1024 * 1024;
});

// Bifrost.Time DI (Phase 00 convention; CI lint fence bans DateTime.UtcNow).
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);

// GuardThresholds — load once at startup (ConfigSet mid-round is logged-but-deferred per
// ADR-0004; the snapshot the GuardChain evaluates against is rebuilt only on
// IterationOpen). Falls back to ADR-0004 defaults when the file is missing so dev
// workflows don't require config/guards.json on disk.
builder.Services.AddSingleton(_ =>
{
    var path = builder.Configuration.GetValue<string>("Gateway:Guards:ConfigPath") ?? "/config/guards.json";
    return GuardThresholds.LoadFromFile(path);
});

// Per-team registry — single Monitor lock; lock-free dictionaries banned per Phase 02 D-09.
builder.Services.AddSingleton<TeamRegistry>();

// IRoundStateSource — Phase 02 seam. For Phase 07 default to ConfigRoundStateSource
// returning RoundOpen until Plan 08 swaps in the RabbitMqRoundStateSource (Phase 06 D-28).
// Test hosts (Bifrost.Gateway.Tests) override this binding via ConfigureTestServices.
builder.Services.AddSingleton<IRoundStateSource>(_ =>
{
    var initial = builder.Configuration.GetValue("RoundState:Initial", "RoundOpen") ?? "RoundOpen";
    return new ConfigRoundStateSource(Enum.Parse<RoundState>(initial, ignoreCase: true));
});

// RabbitMQ resilient connection (Quoter pattern: src/quoter/Program.cs lines 20-43).
// In integration tests the IConnection binding is replaced with a stub before the
// publisher factory runs (Bifrost.Gateway.Tests.Fixtures.GatewayTestHost).
//
// IMPORTANT: the RabbitMq:* config values and the ConnectionFactory MUST be
// resolved INSIDE the DI factory lambda — NOT captured at top-level Program
// scope — because WebApplicationFactory<Program>.ConfigureAppConfiguration
// callbacks (used by the load harness to point this gateway at a
// Testcontainers broker) run during host Build(). A top-level read of
// `builder.Configuration.GetSection("RabbitMq")["Host"]` happens BEFORE
// Build() and therefore observes the appsettings/env value, not the WAF
// in-memory override — which previously caused the load harness to attempt
// connections against the literal string "rabbitmq" on the GH Actions
// runner and fail with BrokerUnreachableException after the Polly
// retry budget elapsed.
builder.Services.AddSingleton<IConnection>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>().GetSection("RabbitMq");
    var factory = new ConnectionFactory
    {
        HostName = cfg["Host"] ?? "rabbitmq",
        Port = int.Parse(cfg["Port"] ?? "5672", CultureInfo.InvariantCulture),
        UserName = cfg["Username"] ?? "guest",
        Password = cfg["Password"] ?? "guest",
    };
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Bifrost.Gateway.Startup");
    var pipeline = RabbitMqResilience.CreateConnectionPipeline(log);
    return pipeline.ExecuteAsync(
        async ct => await factory.CreateConnectionAsync("bifrost-gateway", ct),
        CancellationToken.None).AsTask().GetAwaiter().GetResult();
});

// GatewayCommandPublisher — dedicated IChannel per publisher (Pitfall 6).
builder.Services.AddSingleton<IGatewayCommandPublisher>(sp =>
{
    var conn = sp.GetRequiredService<IConnection>();
    var channel = conn.CreateChannelAsync().GetAwaiter().GetResult();
    return new GatewayCommandPublisher(
        channel,
        sp.GetRequiredService<IClock>(),
        sp.GetRequiredService<ILogger<GatewayCommandPublisher>>());
});

// Bidi gRPC service.
builder.Services.AddSingleton<StrategyGatewayService>();

// Plan 06: PositionTracker + 4 RabbitMQ consumers driving the per-team outbound channels.
// Each consumer creates its OWN IChannel inside ExecuteAsync from the shared IConnection
// (Pitfall 6); each uses AsyncEventingBasicConsumer push subscription (Pitfall 9).
builder.Services.AddSingleton<PositionTracker>();
builder.Services.AddHostedService<PrivateEventConsumer>();
builder.Services.AddHostedService<PublicEventConsumer>();
builder.Services.AddHostedService<AuctionResultConsumer>();
builder.Services.AddHostedService<RoundStateConsumer>();

// Plan 07: ForecastDispatcher (cohort-jittered ForecastUpdate fan-out — GW-08),
// DisconnectHandler (mass-cancel-on-disconnect — GW-07), HeartbeatService (gateway
// liveness — Phase 06 D-19). DisconnectHandler is invoked from
// StrategyGatewayService.finally with a FRESH 2-second CTS (Pitfall 5) and from the
// IHostApplicationLifetime.ApplicationStopping hook below with a 5-second SIGTERM budget.
builder.Services.AddSingleton<DisconnectHandler>();
builder.Services.AddHostedService<Bifrost.Gateway.Dispatch.ForecastDispatcher>();
builder.Services.AddHostedService<HeartbeatService>();

// Phase 00 sentinel: writes /tmp/bifrost-ready when the host is up.
builder.Services.AddHostedService<StartupLogger>();

var app = builder.Build();

// Open Question 2 closure: SIGTERM defensive hook. On `docker stop` (or any other
// host shutdown signal), iterate the registry and run DisconnectHandler with a 5-second
// budget so resting orders don't leak past container shutdown. The per-stream finally
// in StrategyGatewayService also fires its own 2-second mass-cancel, but that path
// only runs when the bidi stream actually tears down — SIGTERM may close streams
// uncleanly, so we redo the sweep at the host level as well.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var registry = app.Services.GetRequiredService<TeamRegistry>();
    var disconnect = app.Services.GetRequiredService<DisconnectHandler>();
    var teams = registry.SnapshotAll();
    if (teams.Length == 0) return;
    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        disconnect.HandleAllAsync(teams, shutdownCts.Token).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILogger<Program>>().LogError(ex, "ApplicationStopping mass-cancel failed");
    }
});

app.UseRouting();
// D-12: prometheus-net /metrics endpoint on the same Kestrel as gRPC.
app.MapMetrics();
app.MapGrpcService<StrategyGatewayService>();
app.Run();

// Expose Program for WebApplicationFactory<Program> in Bifrost.Gateway.Tests
// (Microsoft.AspNetCore.Mvc.Testing convention — top-level statements emit an
// internal Program by default; partial public class flips it for the test fixture).
public partial class Program;
