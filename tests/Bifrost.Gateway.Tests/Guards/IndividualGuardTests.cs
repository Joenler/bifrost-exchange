using Bifrost.Gateway.Guards;
using Bifrost.Gateway.State;
using Bifrost.Time;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using RoundProto = Bifrost.Contracts.Round;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Guards;

public class IndividualGuardTests
{
    // ------------------------------------------------------------------ helpers --

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset Now;
        public MutableClock(DateTimeOffset start) { Now = start; }
        public DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan dt) { Now += dt; }
    }

    private static readonly DateTimeOffset T0 = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    private static (TeamState state, MutableClock clock) Bootstrap()
    {
        var clock = new MutableClock(T0);
        var state = new TeamState("alpha", "team-alpha-1", clock.GetUtcNow());
        return (state, clock);
    }

    private static MarketProto.Instrument H1() => new() { InstrumentId = "H1", DeliveryArea = "DE" };

    private static StrategyProto.StrategyCommand BuildSubmit(
        long qtyTicks = 10_000,
        long priceTicks = 100,
        MarketProto.Side side = MarketProto.Side.Buy,
        string instrumentId = "H1",
        string clientId = "team-alpha-1")
    {
        return new StrategyProto.StrategyCommand
        {
            OrderSubmit = new StrategyProto.OrderSubmit
            {
                ClientId = clientId,
                Instrument = new MarketProto.Instrument { InstrumentId = instrumentId, DeliveryArea = "DE" },
                Side = side,
                OrderType = MarketProto.OrderType.Limit,
                PriceTicks = priceTicks,
                QuantityTicks = qtyTicks,
                ClientOrderId = "co-1",
            },
        };
    }

    private static StrategyProto.StrategyCommand BuildCancel(long orderId = 42, string clientId = "team-alpha-1")
    {
        return new StrategyProto.StrategyCommand
        {
            OrderCancel = new StrategyProto.OrderCancel
            {
                ClientId = clientId,
                OrderId = orderId,
                Instrument = H1(),
            },
        };
    }

    private static StrategyProto.StrategyCommand BuildReplace(long orderId = 42, long newQtyTicks = 0, string clientId = "team-alpha-1")
    {
        return new StrategyProto.StrategyCommand
        {
            OrderReplace = new StrategyProto.OrderReplace
            {
                ClientId = clientId,
                OrderId = orderId,
                NewQuantityTicks = newQtyTicks,
                Instrument = H1(),
            },
        };
    }

    // ============================================================== StructuralGuard --

    [Fact]
    public void StructuralGuard_EmptyOneof_Rejects()
    {
        var cmd = new StrategyProto.StrategyCommand();
        var r = StructuralGuard.Check(cmd);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.Structural, r.Reason);
    }

    [Theory]
    [InlineData("quoter")]
    [InlineData("QUOTER")]
    [InlineData("Quoter")]
    [InlineData("dah-auction")]
    [InlineData("Dah-Auction")]
    [InlineData("DAH-AUCTION")]
    public void StructuralGuard_ReservedClientId_Rejects(string reserved)
    {
        var cmd = BuildSubmit(clientId: reserved);
        var r = StructuralGuard.Check(cmd);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.Structural, r.Reason);
    }

    [Fact]
    public void StructuralGuard_ValidSubmit_Accepts()
    {
        var r = StructuralGuard.Check(BuildSubmit());
        Assert.True(r.Accepted);
    }

    [Fact]
    public void StructuralGuard_BidMatrixSubmit_StructuralReject()
    {
        var cmd = new StrategyProto.StrategyCommand
        {
            BidMatrixSubmit = new StrategyProto.BidMatrixSubmit(),
        };
        var r = StructuralGuard.Check(cmd);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.Structural, r.Reason);
        Assert.Contains("dah-auction", r.Detail);
    }

    [Fact]
    public void StructuralGuard_RegisterMidStream_Rejects()
    {
        var cmd = new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = "alpha" },
        };
        var r = StructuralGuard.Check(cmd);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.Structural, r.Reason);
    }

    [Fact]
    public void StructuralGuard_LimitWithZeroPrice_Rejects()
    {
        var cmd = BuildSubmit(priceTicks: 0);
        var r = StructuralGuard.Check(cmd);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.Structural, r.Reason);
    }

    [Fact]
    public void StructuralGuard_NonPositiveQty_Rejects()
    {
        var cmd = BuildSubmit(qtyTicks: 0);
        var r = StructuralGuard.Check(cmd);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.Structural, r.Reason);
    }

    // ============================================================== StateGateGuard --

    [Theory]
    [InlineData(RoundProto.State.Unspecified)]
    [InlineData(RoundProto.State.IterationOpen)]
    [InlineData(RoundProto.State.AuctionOpen)]
    [InlineData(RoundProto.State.AuctionClosed)]
    [InlineData(RoundProto.State.RoundOpen)]
    [InlineData(RoundProto.State.Gate)]
    [InlineData(RoundProto.State.Settled)]
    [InlineData(RoundProto.State.Aborted)]
    public void StateGateGuard_CancelAllowedAnyState(RoundProto.State round)
    {
        var r = StateGateGuard.Check(BuildCancel(), round);
        Assert.True(r.Accepted);
    }

    [Fact]
    public void StateGateGuard_SubmitOnlyInRoundOpen()
    {
        Assert.True(StateGateGuard.Check(BuildSubmit(), RoundProto.State.RoundOpen).Accepted);
        var rGate = StateGateGuard.Check(BuildSubmit(), RoundProto.State.Gate);
        Assert.False(rGate.Accepted);
        Assert.Equal(StrategyProto.RejectReason.ExchangeClosed, rGate.Reason);
    }

    // ============================================================== MsgRateGuard --

    [Fact]
    public void MsgRateGuard_AtCap_Accepts_OverCap_RateLimits()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();
        // First 500 msg/s pass.
        for (var i = 0; i < t.GatewayMsgRatePerTeam; i++)
        {
            var ok = MsgRateGuard.Check(state, clock, t);
            Assert.True(ok.Accepted, $"msg #{i + 1} should pass at cap");
        }
        // 501st is rejected and sets RateLimitedUntilUtc.
        var fail = MsgRateGuard.Check(state, clock, t);
        Assert.False(fail.Accepted);
        Assert.Equal(StrategyProto.RejectReason.RateLimited, fail.Reason);
        Assert.True(state.RateLimitedUntilUtc > clock.GetUtcNow());
    }

    [Fact]
    public void MsgRateGuard_RateLimitedUntilUtc_ShortCircuitsSubsequentCalls()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();
        state.RateLimitedUntilUtc = clock.GetUtcNow() + TimeSpan.FromMilliseconds(500);

        var r = MsgRateGuard.Check(state, clock, t);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.RateLimited, r.Reason);
    }

    // ============================================================== OtrGuard --

    [Fact]
    public void OtrGuard_AtRatio50_Accepts_OverRatio_RateLimits()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();
        // Seed 1 trade so the OTR denominator is (trades+1) = 2.
        OtrGuard.RecordTrade(state, clock);
        // 100 submits / 2 = 50 → at-ratio (≤). Then 1 more submit → 101/2 = 50.5 > 50 → reject.
        for (var i = 0; i < 100; i++)
        {
            var ok = OtrGuard.Check(state, BuildSubmit(), clock, t);
            Assert.True(ok.Accepted, $"submit #{i + 1} should pass at ratio 50");
        }
        var fail = OtrGuard.Check(state, BuildSubmit(), clock, t);
        Assert.False(fail.Accepted);
        Assert.Equal(StrategyProto.RejectReason.RateLimited, fail.Reason);
        Assert.True(state.RateLimitedUntilUtc > clock.GetUtcNow());
    }

    [Fact]
    public void OtrGuard_CancelDoesNotCount()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var initialCount = state.OtrSubmitsWindow.Count;
        var r = OtrGuard.Check(state, BuildCancel(), clock, t);
        Assert.True(r.Accepted);
        Assert.Equal(initialCount, state.OtrSubmitsWindow.Count);
    }

    // ============================================================== MaxNotionalGuard --

    [Fact]
    public void MaxNotionalGuard_AtCap_50Mwh_Accepts_51_Rejects()
    {
        var t = GuardThresholds.Defaults();
        // QuantityScale.FromTicks: ticks = mwh * 10_000 (TicksPerUnit).
        // 50 MWh = 500_000 ticks; 51 MWh = 510_000 ticks.
        var atCap = MaxNotionalGuard.Check(BuildSubmit(qtyTicks: 500_000), t);
        Assert.True(atCap.Accepted);

        var overCap = MaxNotionalGuard.Check(BuildSubmit(qtyTicks: 510_000), t);
        Assert.False(overCap.Accepted);
        Assert.Equal(StrategyProto.RejectReason.MaxNotional, overCap.Reason);
    }

    [Fact]
    public void MaxNotionalGuard_ReplaceWithUnchangedQty_Skips()
    {
        var t = GuardThresholds.Defaults();
        var r = MaxNotionalGuard.Check(BuildReplace(newQtyTicks: 0), t);
        Assert.True(r.Accepted);
    }

    // ============================================================== MaxOpenOrdersGuard --

    [Fact]
    public void MaxOpenOrdersGuard_50Open_NewSubmit_Rejects()
    {
        var (state, _) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var idxH1 = InstrumentOrdering.IndexOf("H1");
        for (var i = 0; i < t.MaxOpenOrdersPerInstrument; i++)
        {
            state.OpenOrdersByInstrument[idxH1].Add(new OpenOrder(i, $"co-{i}", idxH1, "Buy", 100, 1, 0, T0));
        }
        var r = MaxOpenOrdersGuard.Check(state, BuildSubmit(), t);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.MaxOpenOrders, r.Reason);
    }

    [Fact]
    public void MaxOpenOrdersGuard_Replace_NetZero_Accepts()
    {
        var (state, _) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var idxH1 = InstrumentOrdering.IndexOf("H1");
        // Fill the slot to the cap; Replace of an existing order MUST still pass.
        for (var i = 0; i < t.MaxOpenOrdersPerInstrument; i++)
        {
            state.OpenOrdersByInstrument[idxH1].Add(new OpenOrder(OrderId: 1000 + i, $"co-{i}", idxH1, "Buy", 100, 1, 0, T0));
        }
        var r = MaxOpenOrdersGuard.Check(state, BuildReplace(orderId: 1010), t);
        Assert.True(r.Accepted);
    }

    [Fact]
    public void MaxOpenOrdersGuard_UnknownInstrument_Rejects()
    {
        var (state, _) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var r = MaxOpenOrdersGuard.Check(state, BuildSubmit(instrumentId: "ZZZ"), t);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.UnknownInstrument, r.Reason);
    }

    // ============================================================== MaxPositionGuard --

    [Fact]
    public void MaxPositionGuard_NetTo1000_Accepts_NetTo1001_Rejects()
    {
        var (state, _) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var idxH1 = InstrumentOrdering.IndexOf("H1");
        // Set net to 999 MWh = 9_990_000 ticks. Buy 1 MWh (10_000 ticks) → 1000 MWh accepted.
        state.NetPositionTicks[idxH1] = 9_990_000L;
        var ok = MaxPositionGuard.Check(state, BuildSubmit(qtyTicks: 10_000), t);
        Assert.True(ok.Accepted);

        // Set net to 1000 MWh, then any additional buy goes to 1000+ MWh → reject.
        state.NetPositionTicks[idxH1] = 10_000_000L;
        var fail = MaxPositionGuard.Check(state, BuildSubmit(qtyTicks: 10_000), t);
        Assert.False(fail.Accepted);
        Assert.Equal(StrategyProto.RejectReason.MaxPosition, fail.Reason);
    }

    // ============================================================== SelfTradeGuard --

    [Fact]
    public void SelfTradeGuard_BuyAtSellPrice_Rejects_NewerOrder()
    {
        var (state, _) = Bootstrap();
        var idxH1 = InstrumentOrdering.IndexOf("H1");
        state.OpenOrdersByInstrument[idxH1].Add(new OpenOrder(1, "co-1", idxH1, "Sell", 100, 5, 0, T0));
        // New buy at 100 ≥ resting sell at 100 → cross.
        var r = SelfTradeGuard.Check(state, BuildSubmit(side: MarketProto.Side.Buy, priceTicks: 100));
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.SelfTrade, r.Reason);
    }

    [Fact]
    public void SelfTradeGuard_SameSide_NeverCrosses()
    {
        var (state, _) = Bootstrap();
        var idxH1 = InstrumentOrdering.IndexOf("H1");
        state.OpenOrdersByInstrument[idxH1].Add(new OpenOrder(1, "co-1", idxH1, "Buy", 100, 5, 0, T0));
        // New buy at any price never crosses own resting buy.
        Assert.True(SelfTradeGuard.Check(state, BuildSubmit(side: MarketProto.Side.Buy, priceTicks: 1_000)).Accepted);
        Assert.True(SelfTradeGuard.Check(state, BuildSubmit(side: MarketProto.Side.Buy, priceTicks: 50)).Accepted);
    }
}
