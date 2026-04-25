using System.Globalization;
using Bifrost.Contracts.Internal;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Gateway;
using Bifrost.Gateway.Guards;
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
var rabbitConfig = builder.Configuration.GetSection("RabbitMq");
var factory = new ConnectionFactory
{
    HostName = rabbitConfig["Host"] ?? "rabbitmq",
    Port = int.Parse(rabbitConfig["Port"] ?? "5672", CultureInfo.InvariantCulture),
    UserName = rabbitConfig["Username"] ?? "guest",
    Password = rabbitConfig["Password"] ?? "guest",
};

using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("Bifrost.Gateway.Startup");
var pipeline = RabbitMqResilience.CreateConnectionPipeline(startupLogger);

// Connection creation is wrapped in DI factory (lazy) so the test host can replace
// IConnection before the GatewayCommandPublisher factory runs.
builder.Services.AddSingleton<IConnection>(_ =>
    pipeline.ExecuteAsync(
        async ct => await factory.CreateConnectionAsync("bifrost-gateway", ct),
        CancellationToken.None).AsTask().GetAwaiter().GetResult());

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

// Phase 00 sentinel: writes /tmp/bifrost-ready when the host is up.
builder.Services.AddHostedService<StartupLogger>();

var app = builder.Build();
app.UseRouting();
// D-12: prometheus-net /metrics endpoint on the same Kestrel as gRPC.
app.MapMetrics();
app.MapGrpcService<StrategyGatewayService>();
app.Run();

// Expose Program for WebApplicationFactory<Program> in Bifrost.Gateway.Tests
// (Microsoft.AspNetCore.Mvc.Testing convention — top-level statements emit an
// internal Program by default; partial public class flips it for the test fixture).
public partial class Program;
