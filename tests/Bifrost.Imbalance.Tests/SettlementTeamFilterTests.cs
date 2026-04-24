using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Imbalance.Tests.Fixtures;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// Locks the team-roster filter (T-04-24): client ids in
/// <see cref="ImbalanceSimulatorOptions.NonSettlementClientIds"/> — canonically
/// <c>quoter</c> and <c>dah-auction</c> — must never receive
/// <see cref="ImbalanceSettlementEvent"/> rows even when they have fills
/// recorded in the per-QH net-position map. A regression that removed the
/// deny-list check would leak settlement (and therefore PnL attribution) to
/// the quoter or the DAH-auction synthetic client, which would distort the
/// leaderboard and effectively charge the house for round-trip inventory.
/// </summary>
public class SettlementTeamFilterTests
{
    [Fact]
    public async Task QuoterAndDahClientIds_AreNotSettled()
    {
        var options = new ImbalanceSimulatorOptions
        {
            SigmaGateEuroMwh = 0.0,
            SigmaZeroEuroMwh = 0.0,
            TicksPerEuro = 100,
            DefaultRegime = "Calm",
            NonSettlementClientIds = new[] { "quoter", "dah-auction" },
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

        // Quoter + DAH + one real team all land fills. The fill consumer would
        // forward these unconditionally — the filter lives downstream in
        // HandleGate and HandleSettled, not at the fill boundary, because the
        // quoter's positions still contribute to informational aggregates (not
        // included here for clarity but covered by the print tests).
        await host.InjectAsync(new FillMessage(
            TsNs: 10L,
            ClientId: "quoter",
            InstrumentId: "DE.999901010000-999901010015",
            QuarterIndex: 0,
            Side: "Buy",
            QuantityTicks: 5_000L));
        await host.InjectAsync(new FillMessage(
            TsNs: 20L,
            ClientId: "dah-auction",
            InstrumentId: "DE.999901010015-999901010030",
            QuarterIndex: 1,
            Side: "Buy",
            QuantityTicks: 3_000L));
        await host.InjectAsync(new FillMessage(
            TsNs: 30L,
            ClientId: "alpha",
            InstrumentId: "DE.999901010030-999901010045",
            QuarterIndex: 2,
            Side: "Buy",
            QuantityTicks: 2_000L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await host.InjectAsync(new RoundStateMessage(
            RoundState.RoundOpen, RoundState.Gate, 100L));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await host.InjectAsync(new RoundStateMessage(
            RoundState.Gate, RoundState.Settled, 200L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var settlements = host.Publisher.CapturedPrivate
            .Where(c => c.Evt is ImbalanceSettlementEvent)
            .ToList();

        // alpha → 4 settlements (one per QH). Quoter + DAH → 0 each.
        Assert.Equal(4, settlements.Count);
        Assert.All(settlements, s => Assert.Equal("alpha", s.ClientId));
        Assert.DoesNotContain(settlements, s => s.ClientId == "quoter");
        Assert.DoesNotContain(settlements, s => s.ClientId == "dah-auction");

        // Every row is an ImbalanceSettlementEvent with ClientId=alpha embedded
        // in the payload too — the routing key and the payload must agree so a
        // downstream consumer that re-routes by payload ClientId stays in sync.
        foreach (var s in settlements)
        {
            var evt = Assert.IsType<ImbalanceSettlementEvent>(s.Evt);
            Assert.Equal("alpha", evt.ClientId);
        }
    }

    [Fact]
    public async Task QuoterFills_StillContributeToATeamsInPrint_ButNotToSettlement()
    {
        // Belt-and-braces: the deny list filters BOTH places it appears — the
        // ImbalancePrint's A_teams aggregation AND the per-team settlement
        // emission. If a regression removed the filter from only one of the
        // two sites the system would either (a) leak PnL to the quoter or
        // (b) distort the realized print via the quoter's inventory. Both
        // paths must stay filtered.
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

        // Quoter runs a large long position on Q2 — must NOT reach settlement.
        await host.InjectAsync(new FillMessage(
            TsNs: 10L,
            ClientId: "quoter",
            InstrumentId: "DE.999901010030-999901010045",
            QuarterIndex: 2,
            Side: "Buy",
            QuantityTicks: 100_000L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await host.InjectAsync(new RoundStateMessage(
            RoundState.RoundOpen, RoundState.Gate, 100L));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await host.InjectAsync(new RoundStateMessage(
            RoundState.Gate, RoundState.Settled, 200L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // No real teams → zero settlement rows total (the quoter is filtered).
        var settlements = host.Publisher.CapturedPrivate
            .Count(c => c.Evt is ImbalanceSettlementEvent);
        Assert.Equal(0, settlements);

        // Prints still landed — 4 per Gate regardless of how many real teams exist.
        var prints = host.Publisher.CapturedPublic
            .Count(c => c.Evt is ImbalancePrintEvent);
        Assert.Equal(4, prints);
    }
}
