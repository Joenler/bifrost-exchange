using Bifrost.Gateway.State;
using Bifrost.Time;
using RoundProto = Bifrost.Contracts.Round;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// ADR-0004 6-guard chain (+ structural pre-check + state-gate). CALLER holds
/// <see cref="TeamState.StateLock"/>.
///
/// Order: structural → state-gate → rate/counter (msg-rate, OTR) → absolute
/// (notional, open-orders, position) → cross-order (self-trade). First-failure
/// short-circuits later guards (SPEC req 4).
///
/// Cancel commands skip the rate/counter, absolute, and cross-order tiers per the
/// cancel-bypass invariant (ADR-0004 + Phase 02 D-09 — teams must always be able
/// to flatten exposure).
///
/// PATTERNS Wave 3 anti-pattern: do NOT introduce an IGuard interface or
/// LINQ-pipeline abstraction. The 6 fixed guards are hardcoded ordered method
/// calls so first-failure short-circuit is preserved and the OTR / MsgRate
/// state-update side effects fire only when the chain reaches them.
/// </summary>
internal static class GuardChain
{
    public static GuardResult Evaluate(
        TeamState state,
        StrategyProto.StrategyCommand cmd,
        IClock clock,
        RoundProto.State round,
        GuardThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(thresholds);

        // Tier 1 — structural
        var r = StructuralGuard.Check(cmd);
        if (!r.Accepted) return r;

        // Tier 2 — state-gate (cancel-bypass internal to the guard)
        r = StateGateGuard.Check(cmd, round);
        if (!r.Accepted) return r;

        var isCancel = cmd.CommandCase == StrategyProto.StrategyCommand.CommandOneofCase.OrderCancel;

        // Tier 3 — rate / counter (skipped for OrderCancel)
        if (!isCancel)
        {
            r = MsgRateGuard.Check(state, clock, thresholds);
            if (!r.Accepted) return r;

            r = OtrGuard.Check(state, cmd, clock, thresholds);
            if (!r.Accepted) return r;
        }

        // Tier 4 — absolute (skipped for OrderCancel)
        if (!isCancel)
        {
            r = MaxNotionalGuard.Check(cmd, thresholds);
            if (!r.Accepted) return r;

            r = MaxOpenOrdersGuard.Check(state, cmd, thresholds);
            if (!r.Accepted) return r;

            r = MaxPositionGuard.Check(state, cmd, thresholds);
            if (!r.Accepted) return r;
        }

        // Tier 5 — cross-order (skipped for OrderCancel)
        if (!isCancel)
        {
            r = SelfTradeGuard.Check(state, cmd);
            if (!r.Accepted) return r;
        }

        return GuardResult.Ok;
    }
}
