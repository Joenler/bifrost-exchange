using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Imbalance.Tests.Fixtures;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// D-11 / SPEC Req 10 invariants: at <c>RoundState=Gate</c> the simulator emits
/// exactly four <see cref="ImbalancePrintEvent"/> public messages — one per
/// quarter 0..3, on <c>public.imbalance.print.&lt;instrument_id&gt;</c> — and
/// zero ImbalancePrint messages during any other round state. The print DTO
/// decomposes <c>A_total</c> into the separately visible <c>A_physical</c> so
/// the big-screen can render the physical-shock contribution distinctly from
/// the team-aggregate contribution.
/// </summary>
public class ImbalancePrintEmissionTests
{
    [Fact]
    public async Task Gate_EmitsExactlyFourImbalancePrints_ZeroElsewhere()
    {
        var options = new ImbalanceSimulatorOptions
        {
            SigmaGateEuroMwh = 0.0,
            SigmaZeroEuroMwh = 0.0,
            TicksPerEuro = 100,
            DefaultRegime = "Calm",
            SReferenceTicksPerQuarter = new long[] { 50_000L, 52_000L, 54_000L, 53_000L },
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

        // Before Gate: zero ImbalancePrint events on the public bus.
        var printsBeforeGate = host.Publisher.CapturedPublic
            .Count(c => c.Evt is ImbalancePrintEvent);
        Assert.Equal(0, printsBeforeGate);

        // Transition to Gate.
        await host.InjectAsync(new RoundStateMessage(
            RoundState.RoundOpen, RoundState.Gate, 100L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var printsAtGate = host.Publisher.CapturedPublic
            .Where(c => c.Evt is ImbalancePrintEvent)
            .ToList();

        Assert.Equal(4, printsAtGate.Count);

        // Every print uses the public.imbalance.print.<instrument> routing key
        // shape. No print carries a client-id token on the routing key.
        Assert.All(printsAtGate, p => Assert.StartsWith("public.imbalance.print.", p.RoutingKey));
        Assert.All(printsAtGate, p => Assert.DoesNotContain("private", p.RoutingKey));
        Assert.All(printsAtGate, p =>
            Assert.Equal(MessageTypes.ImbalancePrint, p.MessageType));

        // The four prints cover Q0..Q3 exactly once each.
        var qhs = printsAtGate
            .Select(p => ((ImbalancePrintEvent)p.Evt).QuarterIndex)
            .OrderBy(x => x)
            .ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3 }, qhs);

        // Transition to Settled — no additional prints. Prints are a
        // Gate-only phenomenon; settlements are the private emission at
        // Settled and must not contaminate the print count.
        await host.InjectAsync(new RoundStateMessage(
            RoundState.Gate, RoundState.Settled, 200L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var printsAfterSettled = host.Publisher.CapturedPublic
            .Count(c => c.Evt is ImbalancePrintEvent);
        Assert.Equal(4, printsAfterSettled);
    }

    [Fact]
    public async Task ImbalancePrint_DecomposesATotalIntoTeamAndPhysical()
    {
        // D-12: A_physical is visible on the wire separately from A_total so
        // a consumer can derive A_teams = A_total − A_physical. A regression
        // that stopped populating APhysicalTicks (or merged it into ATotal
        // without decomposition) would surface here.
        var options = new ImbalanceSimulatorOptions
        {
            SigmaGateEuroMwh = 0.0,
            SigmaZeroEuroMwh = 0.0,
            TicksPerEuro = 100,
            DefaultRegime = "Calm",
            K = 50.0,
            Alpha = 1.0,
            NScalingMwh = 100.0,
            GammaCalm = 1.0,
            SReferenceTicksPerQuarter = new long[] { 50_000L, 52_000L, 54_000L, 53_000L },
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

        // Fill: alpha buys 1_000 ticks on Q3 (index 2).
        await host.InjectAsync(new FillMessage(
            TsNs: 10L,
            ClientId: "alpha",
            InstrumentId: "DE.999901010030-999901010045",
            QuarterIndex: 2,
            Side: "Buy",
            QuantityTicks: 1_000L));

        // Round-persistent physical shock: -300 MW on Q3 -> -300 * TicksPerEuro
        // = -30_000 ticks (HandleShock's contribution calc).
        await host.InjectAsync(new ShockMessage(
            TsNs: 20L,
            Mw: -300,
            Label: "gen-trip",
            Persistence: ShockPersistence.Round,
            QuarterIndex: 2));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await host.InjectAsync(new RoundStateMessage(
            RoundState.RoundOpen, RoundState.Gate, 100L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Pick out the Q3 print (QuarterIndex == 2).
        var q3Print = host.Publisher.CapturedPublic
            .Where(c => c.Evt is ImbalancePrintEvent e && e.QuarterIndex == 2)
            .Select(c => (ImbalancePrintEvent)c.Evt)
            .Single();

        // A_physical = -300 * 100 = -30_000.
        Assert.Equal(-30_000L, q3Print.APhysicalTicks);

        // A_total = A_teams + A_physical = 1_000 + (-30_000) = -29_000.
        Assert.Equal(-29_000L, q3Print.ATotalTicks);

        // Round number matches the single RoundOpen we drove through (1).
        Assert.Equal(1, q3Print.RoundNumber);

        // Regime is Calm (default; no regime source wired in the test host).
        Assert.Equal("Calm", q3Print.Regime);

        // TimestampNs comes from the Gate RoundStateMessage we injected.
        Assert.Equal(100L, q3Print.TimestampNs);
    }

    [Fact]
    public async Task ImbalancePrint_CarriesNoClientIdOnRoutingKey()
    {
        // T-04-25: the public print carries no team identity (the ImbalancePrint
        // DTO has no ClientId field by construction — see Plan 3). Reassert at
        // the wire layer that the routing key contains no client-id token,
        // regardless of which teams participated in the round. A regression
        // that added a ClientId-shaped field would surface in the payload
        // reflection too (covered below).
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

        // Two real teams with fills on different QHs.
        await host.InjectAsync(new FillMessage(
            TsNs: 10L, ClientId: "alpha",
            InstrumentId: "DE.999901010000-999901010015",
            QuarterIndex: 0, Side: "Buy", QuantityTicks: 500L));
        await host.InjectAsync(new FillMessage(
            TsNs: 20L, ClientId: "bravo",
            InstrumentId: "DE.999901010045-999901010100",
            QuarterIndex: 3, Side: "Buy", QuantityTicks: 700L));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await host.InjectAsync(new RoundStateMessage(
            RoundState.RoundOpen, RoundState.Gate, 100L));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var prints = host.Publisher.CapturedPublic
            .Where(c => c.Evt is ImbalancePrintEvent)
            .ToList();
        Assert.Equal(4, prints.Count);

        // No team identity token appears in the routing key — not "alpha", not
        // "bravo", not "quoter". Routing keys stay strictly public-topology.
        foreach (var p in prints)
        {
            Assert.DoesNotContain("alpha", p.RoutingKey);
            Assert.DoesNotContain("bravo", p.RoutingKey);
            Assert.DoesNotContain("quoter", p.RoutingKey);
        }

        // Reflection: the payload has no ClientId / Team property.
        var propNames = typeof(ImbalancePrintEvent).GetProperties()
            .Select(p => p.Name.ToLowerInvariant())
            .ToArray();
        Assert.DoesNotContain("clientid", propNames);
        Assert.DoesNotContain("team", propNames);
        Assert.DoesNotContain("teamid", propNames);
    }
}
