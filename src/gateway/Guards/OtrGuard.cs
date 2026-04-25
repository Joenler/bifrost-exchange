using Bifrost.Gateway.State;
using Bifrost.Time;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// Tier 3b of the ADR-0004 chain (#1 in the ADR table). Over a rolling
/// <c>OtrWindowSeconds</c> window (default 60s), enforces
/// <c>orders_submitted / (trades + 1) ≤ OtrMaxRatio</c> (default 50). Submit and
/// Replace count as orders; Cancel does not. Plan 06's PrivateEventConsumer calls
/// <see cref="RecordTrade"/> on every OrderExecutedEvent so the denominator stays
/// current.
///
/// On breach: sets <see cref="TeamState.RateLimitedUntilUtc"/> +
/// <c>OtrTimeoutSeconds</c> (default 1s) and rejects with REJECT_REASON_RATE_LIMITED.
///
/// CALLER holds <see cref="TeamState.StateLock"/>.
/// </summary>
internal static class OtrGuard
{
    public static GuardResult Check(TeamState state, StrategyProto.StrategyCommand cmd, IClock clock, GuardThresholds t)
    {
        // Submit + Replace count as "orders submitted". Cancels do not.
        if (cmd.CommandCase != StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit
         && cmd.CommandCase != StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace)
            return GuardResult.Ok;

        var now = clock.GetUtcNow();
        var windowStart = now - TimeSpan.FromSeconds(t.OtrWindowSeconds);
        while (state.OtrSubmitsWindow.Count > 0 && state.OtrSubmitsWindow.Peek() < windowStart)
            state.OtrSubmitsWindow.Dequeue();
        while (state.OtrTradesWindow.Count > 0 && state.OtrTradesWindow.Peek() < windowStart)
            state.OtrTradesWindow.Dequeue();

        // Append THIS submit before checking — same as MsgRateGuard.
        state.OtrSubmitsWindow.Enqueue(now);

        var orders = state.OtrSubmitsWindow.Count;
        var trades = state.OtrTradesWindow.Count;
        var ratio = (double)orders / (trades + 1);
        if (ratio > t.OtrMaxRatio)
        {
            state.RateLimitedUntilUtc = now + TimeSpan.FromSeconds(t.OtrTimeoutSeconds);
            return GuardResult.Reject(StrategyProto.RejectReason.RateLimited,
                $"OTR {orders}/(trades+1)={ratio:F1} > {t.OtrMaxRatio}");
        }
        return GuardResult.Ok;
    }

    /// <summary>
    /// Plan 06 PrivateEventConsumer calls this on every OrderExecutedEvent so OTR keeps
    /// a current denominator. CALLER holds <see cref="TeamState.StateLock"/>.
    /// </summary>
    public static void RecordTrade(TeamState state, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clock);
        state.OtrTradesWindow.Enqueue(clock.GetUtcNow());
    }
}
