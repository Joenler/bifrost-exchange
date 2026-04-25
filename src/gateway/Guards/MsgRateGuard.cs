using Bifrost.Gateway.State;
using Bifrost.Time;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// Tier 3a of the ADR-0004 chain (#6 in the ADR table). 500 msg/s per team
/// rolling window with 1-second timeout on breach. Also the boundary check for
/// <see cref="TeamState.RateLimitedUntilUtc"/> — every guard chain run begins
/// here, so an existing rate-limit timeout short-circuits the whole chain
/// (PATTERNS.md Wave 3 anti-pattern: re-rejecting every command is wrong;
/// correct shape is gate-on-RateLimitedUntilUtc).
///
/// CALLER holds <see cref="TeamState.StateLock"/>.
/// </summary>
public static class MsgRateGuard
{
    public static GuardResult Check(TeamState state, IClock clock, GuardThresholds t)
    {
        var now = clock.GetUtcNow();

        // Pre-existing rate-limit window — ALL guards short-circuit until it expires.
        if (now < state.RateLimitedUntilUtc)
            return GuardResult.Reject(StrategyProto.RejectReason.RateLimited,
                $"team rate-limited until {state.RateLimitedUntilUtc:O}");

        // Trim entries older than 1 second.
        var oneSecondAgo = now - TimeSpan.FromSeconds(1);
        while (state.MsgRateWindow.Count > 0 && state.MsgRateWindow.Peek() < oneSecondAgo)
            state.MsgRateWindow.Dequeue();

        // Append BEFORE checking — we want this command counted in the rate.
        state.MsgRateWindow.Enqueue(now);
        if (state.MsgRateWindow.Count > t.GatewayMsgRatePerTeam)
        {
            state.RateLimitedUntilUtc = now + TimeSpan.FromSeconds(t.GatewayMsgRateTimeoutSeconds);
            return GuardResult.Reject(StrategyProto.RejectReason.RateLimited,
                $"msg rate {state.MsgRateWindow.Count} > {t.GatewayMsgRatePerTeam} msg/s");
        }
        return GuardResult.Ok;
    }
}
