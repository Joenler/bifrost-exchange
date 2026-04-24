using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Imbalance.Tests.Fixtures;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// Integration tests for SPEC requirements 5 + 6: at <c>RoundState=Settled</c> the
/// simulator emits one <see cref="ImbalanceSettlementEvent"/> per non-deny-list team
/// per quarter (four rows per team, hour instrument excluded). The
/// <c>imbalance_pnl_ticks</c> field is the exact integer product
/// <c>position_ticks × p_imb_ticks</c> — no decimal math, no rounding.
/// <para>
/// Test strategy: drive the full round lifecycle (IterationOpen → AuctionOpen →
/// AuctionClosed → RoundOpen → Gate → Settled) via direct channel injection of
/// <see cref="RoundStateMessage"/> instances. The <see cref="TestImbalanceHost"/>
/// does NOT register <c>RoundStateBridgeHostedService</c>, so
/// <c>host.RoundStateSource.Set</c> would not drive the drain loop —
/// <see cref="TestImbalanceHost.InjectAsync"/> is the correct path.
/// </para>
/// </summary>
public class SettlementEmissionTests
{
    [Fact]
    public async Task ThreeTeamsFourQHs_EmitsTwelveRows_ZeroHourRows()
    {
        // Deterministic options: SigmaGateEuroMwh = 0 suppresses Gaussian noise so
        // the Gate-time P_imb is determined entirely by the reference S_q and the
        // (A_teams + A_physical) penalty. With default K/alpha/N and modest
        // positions the penalty is small but non-zero; the integer equality
        // invariant holds regardless.
        var options = new ImbalanceSimulatorOptions
        {
            K = 50.0,
            Alpha = 1.0,
            NScalingMwh = 100.0,
            GammaCalm = 1.0,
            SigmaGateEuroMwh = 0.0,
            SigmaZeroEuroMwh = 0.0,
            SReferenceTicksPerQuarter = new long[] { 50_000L, 52_000L, 54_000L, 53_000L },
            TicksPerEuro = 100,
            NonSettlementClientIds = new[] { "quoter", "dah-auction" },
            DefaultRegime = "Calm",
        };
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);

        // Drive lifecycle up to RoundOpen so HandleFill's state gate admits fills.
        await host.InjectAsync(new RoundStateMessage(
            RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.AuctionClosed, RoundState.RoundOpen, 2L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // 3 real teams, arbitrary positions spread across Q0..Q3. A couple of
        // zero-position slots are left unfilled on purpose — the settlement emits
        // a full 3×4 matrix anyway because the scoring model expects a row per
        // (team, QH) pair even when that team has no position in that QH.
        var fills = new (string ClientId, int QuarterIndex, long PositionTicks)[]
        {
            ("alpha",   0,  500L),
            ("alpha",   1, -300L),
            ("alpha",   2,  800L),
            // alpha Q3 intentionally unset — expect position=0 in the settlement row.

            ("bravo",   0,  200L),
            ("bravo",   1,  200L),
            ("bravo",   2, -600L),
            ("bravo",   3,  100L),

            // charlie Q0 intentionally unset.
            ("charlie", 1,  400L),
            // charlie Q2 intentionally unset.
            ("charlie", 3,  700L),
        };

        var ts = 10L;
        foreach (var (cid, qh, ticks) in fills)
        {
            // Side string is cosmetic in HandleFill; QuantityTicks is what accumulates.
            await host.InjectAsync(new FillMessage(
                TsNs: ts++,
                ClientId: cid,
                InstrumentId: $"DE.9999010100{qh * 15:D2}-9999010100{(qh + 1) * 15:D2}",
                QuarterIndex: qh,
                Side: ticks >= 0 ? "Buy" : "Sell",
                QuantityTicks: ticks));
        }
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Transition to Gate then Settled.
        await host.InjectAsync(new RoundStateMessage(
            RoundState.RoundOpen, RoundState.Gate, 100L));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await host.InjectAsync(new RoundStateMessage(
            RoundState.Gate, RoundState.Settled, 200L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Extract settlement rows.
        var settlements = host.Publisher.CapturedPrivate
            .Where(c => c.Evt is ImbalanceSettlementEvent)
            .Select(c => (ClientId: c.ClientId, Evt: (ImbalanceSettlementEvent)c.Evt))
            .ToList();

        // 3 teams × 4 QHs = 12 rows; hour instrument (any QH outside 0..3) never
        // appears.
        Assert.Equal(12, settlements.Count);
        Assert.All(settlements, s => Assert.InRange(s.Evt.QuarterIndex, 0, 3));

        // Each (team, QH) pair appears exactly once.
        var pairs = settlements
            .Select(s => (s.ClientId, s.Evt.QuarterIndex))
            .ToHashSet();
        foreach (var cid in new[] { "alpha", "bravo", "charlie" })
        {
            for (var qh = 0; qh < 4; qh++)
            {
                Assert.Contains((cid, qh), pairs);
            }
        }

        // Integer-equality invariant: imbalance_pnl_ticks == position_ticks * p_imb_ticks.
        foreach (var s in settlements)
        {
            Assert.Equal(s.Evt.PositionTicks * s.Evt.PImbTicks, s.Evt.ImbalancePnlTicks);
        }

        // The zero-position rows (alpha Q3, charlie Q0, charlie Q2) still appear
        // with PositionTicks = 0 and PnL = 0.
        var alphaQ3 = settlements.Single(s => s.ClientId == "alpha" && s.Evt.QuarterIndex == 3);
        Assert.Equal(0L, alphaQ3.Evt.PositionTicks);
        Assert.Equal(0L, alphaQ3.Evt.ImbalancePnlTicks);

        var charlieQ0 = settlements.Single(s => s.ClientId == "charlie" && s.Evt.QuarterIndex == 0);
        Assert.Equal(0L, charlieQ0.Evt.PositionTicks);
        Assert.Equal(0L, charlieQ0.Evt.ImbalancePnlTicks);
    }

    [Fact]
    public async Task ImbalancePnl_IsExactIntegerProduct()
    {
        // Focused assertion for SPEC req 6: PnL is the exact checked(position * pimb)
        // product with no intermediate floating-point conversion. Uses a single
        // team with a single non-zero position so the reference is unambiguous.
        var options = new ImbalanceSimulatorOptions
        {
            SigmaGateEuroMwh = 0.0,
            SigmaZeroEuroMwh = 0.0,
            TicksPerEuro = 100,
            DefaultRegime = "Calm",
            NonSettlementClientIds = new[] { "quoter" },
            SReferenceTicksPerQuarter = new long[] { 50_000L, 52_000L, 54_000L, 53_000L },
        };
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);

        await host.InjectAsync(new RoundStateMessage(
            RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.AuctionClosed, RoundState.RoundOpen, 2L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await host.InjectAsync(new FillMessage(
            TsNs: 10L,
            ClientId: "alpha",
            InstrumentId: "DE.999901010015-999901010030",
            QuarterIndex: 1,
            Side: "Buy",
            QuantityTicks: 1_600L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await host.InjectAsync(new RoundStateMessage(
            RoundState.RoundOpen, RoundState.Gate, 100L));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await host.InjectAsync(new RoundStateMessage(
            RoundState.Gate, RoundState.Settled, 200L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var alphaQ2 = host.Publisher.CapturedPrivate
            .Where(c => c.ClientId == "alpha"
                && c.Evt is ImbalanceSettlementEvent e
                && e.QuarterIndex == 1)
            .Select(c => (ImbalanceSettlementEvent)c.Evt)
            .Single();

        Assert.Equal(1_600L, alphaQ2.PositionTicks);
        Assert.Equal(checked(1_600L * alphaQ2.PImbTicks), alphaQ2.ImbalancePnlTicks);
    }

    [Fact]
    public async Task SettledWithoutPriorGate_EmitsZeroSettlements()
    {
        // Defensive invariant: if Settled arrives without a preceding Gate the
        // simulator must not emit settlements against a null / stale price
        // array. The handler logs at Error and drops the emission rather than
        // flowing nonsense rows to the recorder.
        var options = new ImbalanceSimulatorOptions
        {
            SigmaGateEuroMwh = 0.0,
            SigmaZeroEuroMwh = 0.0,
            NonSettlementClientIds = new[] { "quoter" },
        };
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);

        await host.InjectAsync(new RoundStateMessage(
            RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
        await host.InjectAsync(new RoundStateMessage(
            RoundState.AuctionClosed, RoundState.RoundOpen, 2L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await host.InjectAsync(new FillMessage(
            TsNs: 10L,
            ClientId: "alpha",
            InstrumentId: "DE.999901010000-999901010015",
            QuarterIndex: 0,
            Side: "Buy",
            QuantityTicks: 500L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Jump directly from RoundOpen to Settled, bypassing Gate.
        await host.InjectAsync(new RoundStateMessage(
            RoundState.RoundOpen, RoundState.Settled, 200L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var settlements = host.Publisher.CapturedPrivate
            .Count(c => c.Evt is ImbalanceSettlementEvent);
        Assert.Equal(0, settlements);
    }
}
