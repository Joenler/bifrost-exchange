using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Imbalance.HostedServices;
using Bifrost.Imbalance.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// SPEC Req 8 — full 7-state round-lifecycle integration. Drives a single
/// TestImbalanceHost through
/// IterationOpen -> AuctionOpen -> AuctionClosed -> RoundOpen -> Gate ->
/// Settled -> IterationOpen and asserts the total emission profile:
/// <list type="bullet">
///   <item>Zero forecasts during IterationOpen / AuctionOpen / AuctionClosed.</item>
///   <item>Multiple forecasts during RoundOpen at cadence TForecastSeconds.</item>
///   <item>Zero forecast growth after Gate — the state gate in the drain loop
///   suppresses publication even though the PeriodicTimer keeps firing ticks
///   onto the channel.</item>
///   <item>Exactly four ImbalancePrintEvent messages at Gate (one per QH).</item>
///   <item>Exactly N_teams x 4 ImbalanceSettlementEvent rows at Settled.</item>
///   <item>SimulatorState fully cleared (NetPositions empty, APhysicalQh all
///   zero) when a fresh IterationOpen arrives after Settled.</item>
/// </list>
/// <para>
/// Note on state transitions: TestImbalanceHost does NOT register
/// RoundStateBridgeHostedService, so flipping host.RoundStateSource would not
/// drive HandleRoundStateTransition. Transitions are injected directly as
/// RoundStateMessage onto the shared channel — matches the
/// SettlementEmissionTests + ImbalancePrintEmissionTests pattern.
/// </para>
/// </summary>
public class FullRoundCycleIntegrationTests
{
    [Fact]
    public async Task SevenStateCycle_EmissionProfileMatches()
    {
        // Deterministic config: SigmaGate + SigmaZero = 0 so forecast + print
        // payloads are noise-free; any non-determinism in the drain loop would
        // surface as a different integer in the emission stream.
        var options = new ImbalanceSimulatorOptions
        {
            TForecastSeconds = 15,
            RoundDurationSeconds = 60,
            SigmaGateEuroMwh = 0.0,
            SigmaZeroEuroMwh = 0.0,
            TicksPerEuro = 100,
            DefaultRegime = "Calm",
            K = 50.0,
            Alpha = 1.0,
            NScalingMwh = 100.0,
            GammaCalm = 1.0,
            SReferenceTicksPerQuarter = new long[] { 50_000L, 52_000L, 54_000L, 53_000L },
            NonSettlementClientIds = new[] { "quoter", "dah-auction" },
        };
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);
        var timer = new ForecastTimerHostedService(
            host.Time, host.Channel, host.Clock,
            Options.Create(options),
            NullLogger<ForecastTimerHostedService>.Instance);
        await timer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // ---- IterationOpen ---- (initial state) — advance past two cadence
            // intervals; drain-loop state gate must suppress forecast publication.
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 2);
            Assert.DoesNotContain(host.Publisher.CapturedPublic, c => c.Evt is ForecastUpdateEvent);

            // ---- AuctionOpen ----
            await host.InjectAsync(new RoundStateMessage(
                RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 2);
            Assert.DoesNotContain(host.Publisher.CapturedPublic, c => c.Evt is ForecastUpdateEvent);

            // ---- AuctionClosed ----
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 2);
            Assert.DoesNotContain(host.Publisher.CapturedPublic, c => c.Evt is ForecastUpdateEvent);

            // ---- RoundOpen ---- Inject fills, let forecast cadence fire, then
            // assert the count lands in the expected window.
            var roundOpenTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionClosed, RoundState.RoundOpen, roundOpenTs));
            await Task.Delay(50, TestContext.Current.CancellationToken);

            await host.InjectAsync(new FillMessage(
                TsNs: 10L, ClientId: "alpha",
                InstrumentId: "DE.999901010000-999901010015",
                QuarterIndex: 0, Side: "Buy", QuantityTicks: 500L));
            await host.InjectAsync(new FillMessage(
                TsNs: 20L, ClientId: "bravo",
                InstrumentId: "DE.999901010015-999901010030",
                QuarterIndex: 1, Side: "Buy", QuantityTicks: 300L));
            await Task.Delay(50, TestContext.Current.CancellationToken);

            // Advance the full nominal round: 60s / 15s cadence = 4 forecasts
            // expected. Tolerate +/- 1 for FakeTimeProvider step-boundary
            // artefacts in the host's 500 ms Advance loop (mirrors the
            // ForecastCadenceTests.RoundOpen_TenMinutes_EmitsApproximatelyFortyForecasts
            // slack rule).
            await host.AdvanceSecondsAsync(options.RoundDurationSeconds);

            var forecastsDuringRound = host.Publisher.CapturedPublic
                .Count(c => c.Evt is ForecastUpdateEvent);
            Assert.InRange(forecastsDuringRound, 3, 5);

            // ---- Gate ---- Exactly 4 ImbalancePrints.
            var gateTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.RoundOpen, RoundState.Gate, gateTs));
            await Task.Delay(100, TestContext.Current.CancellationToken);

            var prints = host.Publisher.CapturedPublic
                .Where(c => c.Evt is ImbalancePrintEvent)
                .ToList();
            Assert.Equal(4, prints.Count);

            // Forecast count is frozen from here: the drain loop's state gate
            // suppresses publication on every subsequent tick.
            var forecastsFrozenAt = forecastsDuringRound;

            // ---- Settled ---- 2 real teams x 4 QHs = 8 settlement rows.
            // alpha + bravo both recorded fills in RoundOpen so both appear in
            // the realTeams enumeration; quoter is deny-listed.
            var settledTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.Gate, RoundState.Settled, settledTs));
            await Task.Delay(100, TestContext.Current.CancellationToken);

            var settlements = host.Publisher.CapturedPrivate
                .Where(c => c.Evt is ImbalanceSettlementEvent)
                .ToList();
            Assert.Equal(8, settlements.Count);

            // Forecast cadence keeps ticking but the state gate keeps the count
            // frozen across Settled and beyond.
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 2);
            var forecastsAfterSettled = host.Publisher.CapturedPublic
                .Count(c => c.Evt is ForecastUpdateEvent);
            Assert.Equal(forecastsFrozenAt, forecastsAfterSettled);

            // ---- IterationOpen ---- back to baseline. ResetForNewRound clears
            // NetPositions + APhysicalQh + LastPImbTicksPerQuarter.
            await host.InjectAsync(new RoundStateMessage(
                RoundState.Settled, RoundState.IterationOpen, settledTs + 1_000_000_000L));
            await Task.Delay(100, TestContext.Current.CancellationToken);

            Assert.Empty(host.State.NetPositions);
            Assert.All(host.State.APhysicalQh, slot => Assert.Equal(0L, slot));
            Assert.Null(host.State.LastPImbTicksPerQuarter);
        }
        finally
        {
            await timer.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PositionMap_ClearedOnIterationOpen()
    {
        // Focused assertion: the per-team position map is fully cleared by
        // ResetForNewRound when the cycle wraps back to IterationOpen, so a
        // subsequent round cannot bleed stale positions into its settlement.
        await using var host = new TestImbalanceHost(RoundState.IterationOpen);

        await host.InjectAsync(new RoundStateMessage(
            RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.AuctionClosed, RoundState.RoundOpen, 2L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await host.InjectAsync(new FillMessage(
            TsNs: 10L, ClientId: "alpha",
            InstrumentId: "DE.999901010000-999901010015",
            QuarterIndex: 0, Side: "Buy", QuantityTicks: 500L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Single(host.State.NetPositions);

        await host.InjectAsync(new RoundStateMessage(
            RoundState.RoundOpen, RoundState.Gate, 100L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.Gate, RoundState.Settled, 200L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.Settled, RoundState.IterationOpen, 300L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(host.State.NetPositions);
        Assert.All(host.State.APhysicalQh, slot => Assert.Equal(0L, slot));
        Assert.Null(host.State.LastPImbTicksPerQuarter);
    }
}
