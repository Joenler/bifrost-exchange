using Bifrost.Exchange.Application.RoundState;
using Bifrost.Imbalance.Tests.Fixtures;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// Integration tests for SPEC requirement 4: MC-injected physical shocks
/// accumulate into A_physical_QH per-quarter with correct persistence
/// semantics (round-scoped shocks survive through Gate; transient shocks roll
/// off after the configured window).
/// <para>
/// Test strategy: drive <see cref="ShockMessage"/> instances directly onto
/// the shared channel via <see cref="TestImbalanceHost.InjectAsync"/> and
/// assert on the actor loop's resulting <see cref="SimulatorState.APhysicalQh"/>
/// slots. This exercises the accumulator shape without bringing the RabbitMQ
/// wire layer into the test surface — the wire layer (routing key binding,
/// envelope deserialization, defensive range-check at the consumer boundary)
/// is covered structurally by <c>ShockConsumerHostedService</c>'s own
/// boundary guards and will be exercised end-to-end by the phase-level
/// integration-test plan.
/// </para>
/// <para>
/// RoundState transitions are enqueued as <see cref="RoundStateMessage"/>
/// directly rather than via <c>MockRoundStateSource.Set</c>: the bridge
/// hosted service that translates <c>IRoundStateSource.OnChange</c> events
/// into channel messages lands in a later plan, so the test pre-stages what
/// that bridge will eventually do in production. Same convention the fill
/// accumulator tests follow.
/// </para>
/// </summary>
public class PhysicalShockAccumulatorTests
{
    [Fact]
    public async Task RoundShock_QuarterIndex2_AffectsOnlyQ2AndSurvivesTransientWindow()
    {
        // SPEC-4 acceptance: a round-persistent shock on a single QH
        // increments A_physical only for that QH and persists beyond the
        // transient rolloff window — it survives until Settled (which is
        // modelled by ExpireTransientShocks not touching it).
        var options = new ImbalanceSimulatorOptions { TicksPerEuro = 100, TTransientSeconds = 30 };
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);

        // Transition to RoundOpen via channel messages so HandleShock's
        // CurrentRoundState gate flips open.
        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.IterationOpen,
            Current: RoundState.AuctionOpen,
            TsNs: 0L));
        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.AuctionOpen,
            Current: RoundState.AuctionClosed,
            TsNs: 1L));
        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.AuctionClosed,
            Current: RoundState.RoundOpen,
            TsNs: 2L));

        // Fire a Round-persistent shock at t = 60s: -300 MW on Q3 (index 2).
        // At TicksPerEuro = 100: contribution = -300 * 100 = -30_000 ticks.
        await host.InjectAsync(new ShockMessage(
            TsNs: 60_000_000_000L,
            Mw: -300,
            Label: "gen-trip",
            Persistence: ShockPersistence.Round,
            QuarterIndex: 2));

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert: only index 2 carries the shock; other slots untouched.
        Assert.Equal(0L, host.State.APhysicalQh[0]);
        Assert.Equal(0L, host.State.APhysicalQh[1]);
        Assert.Equal(-30_000L, host.State.APhysicalQh[2]);
        Assert.Equal(0L, host.State.APhysicalQh[3]);

        // Round-persistent shocks are not added to PendingTransients — they
        // are committed directly to A_physical and do not roll off.
        Assert.Empty(host.State.PendingTransients);

        // Fire a forecast tick well past the 30s transient window. This runs
        // ExpireTransientShocks. A round-persistent shock must NOT be
        // subtracted off on rolloff; A_physical[2] stays at -30_000.
        await host.InjectAsync(new ForecastTickMessage(120_000_000_000L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(-30_000L, host.State.APhysicalQh[2]);
        Assert.Empty(host.State.PendingTransients);
    }

    [Fact]
    public async Task TransientShock_ContributesImmediatelyAndRollsOffAfterWindow()
    {
        // SPEC-4 acceptance: a transient shock contributes to A_physical
        // immediately but is subtracted off after TTransientSeconds elapses.
        // The rolloff is driven by ExpireTransientShocks on every forecast
        // tick — a tick past the window decrements A_physical for the
        // matching QH.
        var options = new ImbalanceSimulatorOptions { TicksPerEuro = 100, TTransientSeconds = 30 };
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);

        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.IterationOpen,
            Current: RoundState.AuctionOpen,
            TsNs: 0L));
        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.AuctionOpen,
            Current: RoundState.AuctionClosed,
            TsNs: 1L));
        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.AuctionClosed,
            Current: RoundState.RoundOpen,
            TsNs: 2L));

        // Fire a Transient shock at t = 60s: -100 MW on Q2 (index 1).
        // Contribution = -100 * 100 = -10_000 ticks.
        await host.InjectAsync(new ShockMessage(
            TsNs: 60_000_000_000L,
            Mw: -100,
            Label: "brief-surge",
            Persistence: ShockPersistence.Transient,
            QuarterIndex: 1));

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Immediately: Q2 carries the contribution and PendingTransients
        // tracks the rolloff candidate.
        Assert.Equal(-10_000L, host.State.APhysicalQh[1]);
        Assert.Single(host.State.PendingTransients);

        var pending = host.State.PendingTransients[0];
        Assert.Equal(1, pending.QuarterIndex);
        Assert.Equal(-10_000L, pending.ContributionTicks);
        Assert.Equal(30L * 1_000_000_000L, pending.TransientWindowNs);

        // Forecast tick at t = 120s (60s past activation, well past the 30s
        // window). ExpireTransientShocks subtracts the contribution off
        // A_physical[1] and removes the pending entry.
        await host.InjectAsync(new ForecastTickMessage(120_000_000_000L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(0L, host.State.APhysicalQh[1]);
        Assert.Empty(host.State.PendingTransients);
    }

    [Fact]
    public async Task TransientShock_StillInsideWindow_DoesNotRollOff()
    {
        // Complement to the rolloff test: a forecast tick BEFORE the
        // transient window elapses must NOT subtract the contribution.
        // Proves ExpireTransientShocks respects the (now - activated) >=
        // window invariant rather than tearing down on any tick.
        var options = new ImbalanceSimulatorOptions { TicksPerEuro = 100, TTransientSeconds = 30 };
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);

        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.IterationOpen,
            Current: RoundState.AuctionOpen,
            TsNs: 0L));
        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.AuctionOpen,
            Current: RoundState.AuctionClosed,
            TsNs: 1L));
        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.AuctionClosed,
            Current: RoundState.RoundOpen,
            TsNs: 2L));

        await host.InjectAsync(new ShockMessage(
            TsNs: 60_000_000_000L,
            Mw: 200,
            Label: "brief-renewables-peak",
            Persistence: ShockPersistence.Transient,
            QuarterIndex: 3));

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(20_000L, host.State.APhysicalQh[3]);
        Assert.Single(host.State.PendingTransients);

        // Forecast tick at t = 75s (15s past activation, still well inside
        // the 30s window). Contribution must persist.
        await host.InjectAsync(new ForecastTickMessage(75_000_000_000L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(20_000L, host.State.APhysicalQh[3]);
        Assert.Single(host.State.PendingTransients);
    }

    [Fact]
    public async Task ShocksOutsideRoundOpen_AreIgnoredDefensively()
    {
        // HandleShock gates on CurrentRoundState == RoundOpen. A shock
        // published outside the round window (orchestrator bug, MC
        // misfiring between rounds) must not corrupt A_physical.
        await using var host = new TestImbalanceHost(RoundState.IterationOpen);

        await host.InjectAsync(new ShockMessage(
            TsNs: 1L,
            Mw: -500,
            Label: "between-rounds",
            Persistence: ShockPersistence.Round,
            QuarterIndex: 0));

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.All(host.State.APhysicalQh, slot => Assert.Equal(0L, slot));
        Assert.Empty(host.State.PendingTransients);
    }

    [Fact]
    public async Task OutOfRangeQuarterIndex_IsDefensivelyDropped()
    {
        // D-09 defense-in-depth: the publisher-boundary invariant is that
        // every physical-shock event carries a valid quarter index 0..3.
        // The simulator HandleShock arm reasserts the range guard as a
        // release-mode line of defence — an out-of-range shock is logged
        // and dropped without state mutation. This test reaches the guard
        // by direct-injecting a ShockMessage onto the channel (bypassing
        // the consumer's own boundary drop) to prove the drain loop's
        // guard holds even when a wire-side regression lets one through.
        //
        // Debug.Assert behaviour note: HandleShock also carries a
        // Debug.Assert on the same invariant. In Release builds (the
        // plan-mandated verification config) Debug.Assert compiles out,
        // so only the release range guard fires. In Debug builds, a
        // Debug.Assert failure throws, which the drain loop's per-message
        // try/catch isolates — state still does not mutate, the loop
        // keeps running, and this assertion still holds.
        await using var host = new TestImbalanceHost(RoundState.IterationOpen);

        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.IterationOpen,
            Current: RoundState.AuctionOpen,
            TsNs: 0L));
        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.AuctionOpen,
            Current: RoundState.AuctionClosed,
            TsNs: 1L));
        await host.InjectAsync(new RoundStateMessage(
            Previous: RoundState.AuctionClosed,
            Current: RoundState.RoundOpen,
            TsNs: 2L));

        await host.InjectAsync(new ShockMessage(
            TsNs: 1L, Mw: -500, Label: "bad",
            Persistence: ShockPersistence.Round, QuarterIndex: 7));

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // A_physical_QH must remain all-zero; PendingTransients must stay
        // empty. No state mutation occurred on the out-of-range entry.
        Assert.All(host.State.APhysicalQh, slot => Assert.Equal(0L, slot));
        Assert.Empty(host.State.PendingTransients);
    }
}
