using System.Globalization;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Quoter;
using Bifrost.Quoter.Abstractions;
using Bifrost.Quoter.Mocks;
using Bifrost.Quoter.Pricing;
using Bifrost.Quoter.Rabbit;
using Bifrost.Quoter.Schedule;
using Bifrost.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

var builder = Host.CreateApplicationBuilder(args);

// ---------- RabbitMQ connection (Polly-retried via shared resilience pipeline) ----------
// Single connection shared by all quoter Rabbit components (publisher channel +
// MC regime-force consumer). Matches Phase 02 exchange Program.cs pattern; the
// resilience pipeline tolerates the cold-boot race where the quoter container
// starts polling before rabbit's AMQP listener is ready.
var rabbitConfig = builder.Configuration.GetSection("RabbitMq");
var factory = new ConnectionFactory
{
    HostName = rabbitConfig["Host"] ?? "rabbitmq",
    Port = int.Parse(rabbitConfig["Port"] ?? "5672", CultureInfo.InvariantCulture),
    UserName = rabbitConfig["Username"] ?? "guest",
    Password = rabbitConfig["Password"] ?? "guest",
};

using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("Bifrost.Quoter.Startup");
var pipeline = RabbitMqResilience.CreateConnectionPipeline(startupLogger);

var connection = await pipeline.ExecuteAsync(
    async ct => await factory.CreateConnectionAsync("bifrost-quoter", ct),
    CancellationToken.None);
builder.Services.AddSingleton(connection);

// Clock seam (every clock access goes through IClock; tests inject FakeTimeProvider).
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);

// Round-state seam — dev/test reads the initial state from appsettings; the
// future RabbitMqRoundStateSource swaps in here once the orchestrator publishes
// round transitions.
builder.Services.AddSingleton<IRoundStateSource>(_ =>
    new ConfigRoundStateSource(
        Enum.Parse<RoundState>(builder.Configuration["RoundState:Initial"] ?? "RoundOpen")));

// Options binding for the QuoterConfig section in appsettings.json.
builder.Services.Configure<QuoterConfig>(builder.Configuration.GetSection("Quoter"));

// Imbalance-truth seam — constant-returning mock; the real implementation
// lands alongside the imbalance simulator and replaces this binding without
// touching call sites.
builder.Services.AddSingleton<IImbalanceTruthView>(_ =>
    new MockImbalanceTruthView(
        builder.Configuration.GetValue<long>("Quoter:MockTruthPriceTicks", 5000)));

// Static instrument set -- 1 hour + 4 quarters with synthetic 9999 delivery,
// matching the matching-engine boot set. Phase 06 orchestrator will own
// rotation if real round calendars ever land.
builder.Services.AddSingleton<IEnumerable<InstrumentId>>(_ => TradingCalendar.GenerateInstruments());

// Scenario + RegimeSchedule. Scenario seed flows into both the schedule's
// Markov RNG (XOR'd with 0xC0DECAFE) and the GBM model's per-instrument RNGs.
builder.Services.AddSingleton<Scenario>(sp =>
{
    var path = builder.Configuration["Quoter:ScenarioPath"]
        ?? throw new InvalidOperationException("Quoter:ScenarioPath not configured.");
    return ScenarioLoader.Load(path);
});

builder.Services.AddSingleton<RegimeSchedule>(sp =>
    new RegimeSchedule(
        sp.GetRequiredService<Scenario>(),
        sp.GetRequiredService<IClock>().GetUtcNow()));

// MC-force inbox -- bounded channel; the McRegimeForceConsumer is the only
// writer, the quoter loop is the only reader.
builder.Services.AddSingleton<Channel<RegimeForceMessage>>(_ =>
    Channel.CreateBounded<RegimeForceMessage>(64));

// Buffered event publisher -- Phase 02 donation, channel-backed wrapper around
// RabbitMqEventPublisher. Each AMQP publisher owns its own dedicated IChannel
// because RabbitMQ.Client 7.x channels are NOT thread-safe and the buffered
// publisher's drain task runs concurrently with the quoter's tick loop.
builder.Services.AddSingleton(sp =>
{
    var conn = sp.GetRequiredService<IConnection>();
    var eventsChannel = conn.CreateChannelAsync().GetAwaiter().GetResult();
    var clock = sp.GetRequiredService<IClock>();
    return new Bifrost.Exchange.Infrastructure.RabbitMq.RabbitMqEventPublisher(eventsChannel, clock);
});

builder.Services.AddSingleton(sp =>
    new Bifrost.Exchange.Infrastructure.RabbitMq.BufferedEventPublisher(
        sp.GetRequiredService<Bifrost.Exchange.Infrastructure.RabbitMq.RabbitMqEventPublisher>(),
        sp.GetService<ILogger<Bifrost.Exchange.Infrastructure.RabbitMq.BufferedEventPublisher>>()));

// Regime-change publisher -- RabbitMQ-backed via Phase 02 BufferedEventPublisher.
// Emits Event.RegimeChange on bifrost.public with routing key
// events.regime.change. Non-blocking: BufferedEventPublisher's drain task
// owns the AMQP I/O so the quoter's regime-transition critical section never
// stalls on broker round-trips.
builder.Services.AddSingleton<IRegimeChangePublisher, RegimeChangePublisher>();

// Pricing primitives. The GBM model is constructed against the scenario seed
// so replays are bit-for-bit identical.
builder.Services.AddSingleton(sp =>
{
    var scenario = sp.GetRequiredService<Scenario>();
    var instruments = sp.GetRequiredService<IEnumerable<InstrumentId>>().ToList();
    var gbmConfig = new GbmConfig(
        DefaultSeedPriceTicks: builder.Configuration.GetValue<long>("Quoter:MockTruthPriceTicks", 5000),
        Seed: scenario.Seed);
    return new GbmPriceModel(gbmConfig, instruments);
});

builder.Services.AddSingleton(_ => new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System));

// IOrderContext live binding -- QuoterCommandPublisher publishes order commands
// to the Phase 02 internal RabbitMQ command fabric (bifrost.cmd) with
// ClientId = "quoter". Owns a dedicated IChannel because RabbitMQ.Client 7.x
// channels are not thread-safe and this channel must not be shared with the
// BufferedEventPublisher's drain loop.
builder.Services.AddSingleton<IOrderContext>(sp =>
{
    var conn = sp.GetRequiredService<IConnection>();
    var commandChannel = conn.CreateChannelAsync().GetAwaiter().GetResult();
    var clock = sp.GetRequiredService<IClock>();
    var log = sp.GetRequiredService<ILogger<QuoterCommandPublisher>>();
    return new QuoterCommandPublisher(commandChannel, clock, log);
});

// Inbound MC regime-force consumer (poll-mode BackgroundService).
builder.Services.AddHostedService<McRegimeForceConsumer>();

// Sentinel HEALTHCHECK retained from the bootstrap skeleton; writes /tmp/bifrost-ready
// as soon as the host starts so docker compose can mark this service healthy.
builder.Services.AddHostedService<StartupLogger>();

// Main quoter loop.
builder.Services.AddHostedService<Quoter>();

var host = builder.Build();
await host.RunAsync();
