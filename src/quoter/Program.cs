using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Quoter;
using Bifrost.Quoter.Abstractions;
using Bifrost.Quoter.Mocks;
using Bifrost.Quoter.Pricing;
using Bifrost.Quoter.Stubs;
using Bifrost.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

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

// Imbalance-truth seam — wave-2 ships the constant-returning mock; the real
// implementation lands alongside the imbalance simulator and replaces this
// binding without touching call sites.
builder.Services.AddSingleton<IImbalanceTruthView>(_ =>
    new MockImbalanceTruthView(
        builder.Configuration.GetValue<long>("Quoter:MockTruthPriceTicks", 5000)));

// Pricing primitives. Wave-3 will replace these placeholder constructions once the
// scenario loader supplies real seed + initial-mid values per instrument.
builder.Services.AddSingleton(_ =>
{
    var instruments = TradingCalendar.GenerateInstruments();
    // TODO(scenario): GbmConfig values come from the scenario file once the loader lands.
    var gbmConfig = new GbmConfig(DefaultSeedPriceTicks: 5000, Seed: 0);
    return new GbmPriceModel(gbmConfig, instruments);
});

builder.Services.AddSingleton(_ => new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System));

// IOrderContext stub — kept under src/quoter/Stubs/ so the QuoterCommandPublisher
// swap-in can unconditionally delete the file. This is the deliberate build-green
// shim while the RabbitMQ command publisher is offline.
builder.Services.AddSingleton<IOrderContext, NoOpOrderContext>();

// Sentinel HEALTHCHECK retained from the bootstrap skeleton; writes /tmp/bifrost-ready
// as soon as the host starts so docker compose can mark this service healthy.
builder.Services.AddHostedService<StartupLogger>();

// Main quoter loop.
builder.Services.AddHostedService<Quoter>();

var host = builder.Build();
await host.RunAsync();
