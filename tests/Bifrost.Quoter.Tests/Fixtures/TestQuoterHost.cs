using System.Threading.Channels;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Quoter;
using Bifrost.Quoter.Mocks;
using Bifrost.Quoter.Pricing;
using Bifrost.Quoter.Schedule;
using Bifrost.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Quoter.Tests.Fixtures;

/// <summary>
/// Composition-root helper for the Quoter integration tests. Wires the Quoter
/// against a <see cref="FakeTimeProvider"/>-driven clock + PeriodicTimer, an
/// <see cref="InMemoryRoundStateSource"/> that the test can flip mid-run, the
/// constant <see cref="MockImbalanceTruthView"/>, the live RegimeSchedule + GBM
/// + PyramidQuoteTracker primitives, and a <see cref="TestRabbitPublisher"/>
/// substituted for both the order-context and regime-change publisher seams.
/// <para>
/// The <see cref="AdvanceSecondsAsync"/> helper steps the FakeTimeProvider in
/// 500ms increments (matching the Quoter's GbmStepMs default) and yields
/// between steps so the PeriodicTimer's WaitForNextTickAsync gets a chance to
/// release the awaiter and execute the tick body.
/// </para>
/// <para>
/// The 5-instrument set is sourced from <c>TradingCalendar.GenerateInstruments()</c>
/// (the same static Phase 02 set the matching engine boots against) so the
/// quoter sees identical instruments to production wiring.
/// </para>
/// </summary>
public sealed class TestQuoterHost : IAsyncDisposable
{
    public FakeTimeProvider Time { get; }
    public InMemoryRoundStateSource RoundStateSource { get; }
    public MockImbalanceTruthView Truth { get; }
    public Scenario Scenario { get; }
    public RegimeSchedule Schedule { get; }
    public GbmPriceModel Gbm { get; }
    public PyramidQuoteTracker Tracker { get; }
    public TestRabbitPublisher TestPublisher { get; }
    public Channel<RegimeForceMessage> Inbox { get; }
    public IReadOnlyList<InstrumentId> Instruments { get; }
    public global::Bifrost.Quoter.Quoter Quoter { get; }
    public QuoterConfig Config { get; }

    public TestQuoterHost(
        string scenarioFileName,
        int? overrideSeed = null,
        RoundState initialState = RoundState.RoundOpen,
        long truthPriceTicks = 5000)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero));
        RoundStateSource = new InMemoryRoundStateSource(initialState);
        Truth = new MockImbalanceTruthView(truthPriceTicks);

        var scenarioPath = Path.Combine(AppContext.BaseDirectory, "TestScenarios", scenarioFileName);
        var loaded = ScenarioLoader.Load(scenarioPath);
        Scenario = overrideSeed is { } seed ? loaded with { Seed = seed } : loaded;

        Schedule = new RegimeSchedule(Scenario, Time.GetUtcNow());

        Instruments = TradingCalendar.GenerateInstruments();
        var gbmConfig = new GbmConfig(
            DefaultSeedPriceTicks: truthPriceTicks,
            Seed: Scenario.Seed,
            Dt: 0.0005);
        Gbm = new GbmPriceModel(gbmConfig, Instruments);

        Tracker = new PyramidQuoteTracker(maxLevels: 3, Time);

        TestPublisher = new TestRabbitPublisher();
        Inbox = Channel.CreateBounded<RegimeForceMessage>(64);

        Config = new QuoterConfig
        {
            GbmStepMs = 500,
            MockTruthPriceTicks = truthPriceTicks,
        };

        var clock = new TestClock(Time);
        Quoter = new global::Bifrost.Quoter.Quoter(
            clock,
            Time,
            RoundStateSource,
            Truth,
            Gbm,
            Tracker,
            commandCtx: TestPublisher,
            schedule: Schedule,
            inbox: Inbox,
            regimeEvents: TestPublisher,
            instruments: Instruments,
            config: Options.Create(Config),
            log: NullLogger<global::Bifrost.Quoter.Quoter>.Instance);
    }

    /// <summary>
    /// Advance the FakeTimeProvider by <paramref name="seconds"/> in 500ms
    /// increments (matching the Quoter's GbmStepMs default cadence). Each
    /// step yields the scheduler so the awaiting PeriodicTimer.WaitForNextTickAsync
    /// continuation can run before the next Advance.
    /// </summary>
    public async Task AdvanceSecondsAsync(double seconds)
    {
        // Each tick is 500ms by default. Round up so callers asking for a
        // sub-tick advance still get at least one tick.
        var steps = Math.Max(1, (int)Math.Ceiling(seconds * 2.0));
        for (var i = 0; i < steps; i++)
        {
            Time.Advance(TimeSpan.FromMilliseconds(500));
            // Yield twice: once for the timer continuation, once for any
            // chained continuation inside the tick body.
            await Task.Yield();
            await Task.Yield();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Quoter.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort stop; tests are expected to call StopAsync explicitly.
        }
    }

    /// <summary>
    /// Bifrost.Time.IClock adapter backed by the host's FakeTimeProvider for
    /// deterministic clock reads inside the Quoter ExecuteAsync loop.
    /// </summary>
    private sealed class TestClock : IClock
    {
        private readonly FakeTimeProvider _time;
        public TestClock(FakeTimeProvider time) => _time = time;
        public DateTimeOffset GetUtcNow() => _time.GetUtcNow();
    }
}
