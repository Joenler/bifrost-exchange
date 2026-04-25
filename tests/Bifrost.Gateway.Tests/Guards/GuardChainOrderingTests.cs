using Bifrost.Gateway.Guards;
using Bifrost.Gateway.State;
using Bifrost.Time;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using RoundProto = Bifrost.Contracts.Round;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Guards;

public class GuardChainOrderingTests
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

    private static StrategyProto.StrategyCommand BuildValidSubmit(long qtyTicks = 10_000) =>
        new()
        {
            OrderSubmit = new StrategyProto.OrderSubmit
            {
                ClientId = "team-alpha-1",
                Instrument = new MarketProto.Instrument { InstrumentId = "H1", DeliveryArea = "DE" },
                Side = MarketProto.Side.Buy,
                OrderType = MarketProto.OrderType.Limit,
                PriceTicks = 100,
                QuantityTicks = qtyTicks,
                ClientOrderId = "co-1",
            },
        };

    private static StrategyProto.StrategyCommand BuildCancel() =>
        new()
        {
            OrderCancel = new StrategyProto.OrderCancel
            {
                ClientId = "team-alpha-1",
                OrderId = 1,
                Instrument = new MarketProto.Instrument { InstrumentId = "H1", DeliveryArea = "DE" },
            },
        };

    // ============================================================== ordering tests --

    [Fact]
    public void StructuralReject_TakesPriorityOver_PositionReject()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var idxH1 = InstrumentOrdering.IndexOf("H1");
        // Pre-load an extreme net position so MaxPosition would also reject if reached.
        state.NetPositionTicks[idxH1] = 100_000_000L;   // 10_000 MWh — well over 1000 cap.

        // Build a malformed Submit: quantity 0 → structural failure.
        var cmd = new StrategyProto.StrategyCommand
        {
            OrderSubmit = new StrategyProto.OrderSubmit
            {
                ClientId = "team-alpha-1",
                Instrument = new MarketProto.Instrument { InstrumentId = "H1", DeliveryArea = "DE" },
                Side = MarketProto.Side.Buy,
                OrderType = MarketProto.OrderType.Limit,
                PriceTicks = 100,
                QuantityTicks = 0,
            },
        };
        var r = GuardChain.Evaluate(state, cmd, clock, RoundProto.State.RoundOpen, t);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.Structural, r.Reason);
    }

    [Fact]
    public void StateGate_RejectsSubmit_During_Gate_WithExchangeClosed()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var r = GuardChain.Evaluate(state, BuildValidSubmit(), clock, RoundProto.State.Gate, t);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.ExchangeClosed, r.Reason);
    }

    [Fact]
    public void StateGate_AllowsCancel_During_Gate_CancelBypass()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var r = GuardChain.Evaluate(state, BuildCancel(), clock, RoundProto.State.Gate, t);
        Assert.True(r.Accepted);
    }

    [Fact]
    public void Cancel_DuringSettled_StillAccepted_CancelBypass()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var r = GuardChain.Evaluate(state, BuildCancel(), clock, RoundProto.State.Settled, t);
        Assert.True(r.Accepted);
    }

    [Fact]
    public void RateLimit_WindowShortCircuitsSubsequentCommands()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();

        // Force a rate-limit by submitting 501 msg/s.
        for (var i = 0; i < t.GatewayMsgRatePerTeam; i++)
        {
            var ok = MsgRateGuard.Check(state, clock, t);
            Assert.True(ok.Accepted);
        }
        var trip = MsgRateGuard.Check(state, clock, t);
        Assert.False(trip.Accepted);
        Assert.True(state.RateLimitedUntilUtc > clock.GetUtcNow());

        // Next command via the full chain inside the 1-second window short-circuits at MsgRate.
        var r = GuardChain.Evaluate(state, BuildValidSubmit(), clock, RoundProto.State.RoundOpen, t);
        Assert.False(r.Accepted);
        Assert.Equal(StrategyProto.RejectReason.RateLimited, r.Reason);

        // Advance time past the timeout — next command must accept again.
        // Trim the existing windows by also advancing past the 1-second msg-rate window
        // (the trim-on-call inside MsgRateGuard purges entries < now-1s).
        clock.Advance(TimeSpan.FromSeconds(t.GatewayMsgRateTimeoutSeconds + 2));
        var r2 = GuardChain.Evaluate(state, BuildValidSubmit(), clock, RoundProto.State.RoundOpen, t);
        Assert.True(r2.Accepted);
    }

    [Fact]
    public void ConfigSet_MidRound_DoesNotChangeLiveThresholds()
    {
        var (state, clock) = Bootstrap();

        // Snapshot captured at "round start"; would be rebuilt only at IterationOpen per ADR-0004.
        var snapshot = GuardThresholds.Defaults();

        // ConfigSet would mutate a different GuardThresholds instance; live evaluation
        // continues to use the snapshot. We model this by NOT passing the modified copy.
        // Drop the cap to 0 MWh — guarantees a 1-MWh submit rejects under `modified` only.
        var modified = snapshot with { MaxOrderNotionalMwh = 0 };

        // Live behavior: pass the SNAPSHOT (frozen at IterationOpen). Should still accept.
        var ok = GuardChain.Evaluate(state, BuildValidSubmit(qtyTicks: 10_000), clock, RoundProto.State.RoundOpen, snapshot);
        Assert.True(ok.Accepted);

        // If by mistake the modified copy were passed, the same submit would reject.
        // (Use a fresh state so msg-rate window from the prior call is clean.)
        var (state2, clock2) = Bootstrap();
        var fail = GuardChain.Evaluate(state2, BuildValidSubmit(qtyTicks: 10_000), clock2, RoundProto.State.RoundOpen, modified);
        Assert.False(fail.Accepted);
        Assert.Equal(StrategyProto.RejectReason.MaxNotional, fail.Reason);
    }

    [Fact]
    public void ValidSubmit_DuringRoundOpen_PassesEntireChain()
    {
        var (state, clock) = Bootstrap();
        var t = GuardThresholds.Defaults();
        var r = GuardChain.Evaluate(state, BuildValidSubmit(), clock, RoundProto.State.RoundOpen, t);
        Assert.True(r.Accepted);
    }
}
