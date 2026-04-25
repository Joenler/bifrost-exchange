using Bifrost.Gateway.State;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// Tier 4b of the ADR-0004 chain (#2 in the ADR table). ≤ 50 open orders per
/// (team, instrument). Submit + Replace are subject; Cancel is not. Replace counts
/// as the new order taking the slot of the old order — net change zero — so a
/// Replace passes if the old order is still in <see cref="TeamState.OpenOrdersByInstrument"/>;
/// otherwise it is treated as a fresh submit at this guard's check.
///
/// CALLER holds <see cref="TeamState.StateLock"/>.
/// </summary>
internal static class MaxOpenOrdersGuard
{
    public static GuardResult Check(TeamState state, StrategyProto.StrategyCommand cmd, GuardThresholds t)
    {
        if (cmd.CommandCase != StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit
         && cmd.CommandCase != StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace)
            return GuardResult.Ok;

        var instrumentId = ExtractInstrumentId(cmd);
        var idx = InstrumentOrdering.IndexOf(instrumentId);
        if (idx < 0)
            return GuardResult.Reject(StrategyProto.RejectReason.UnknownInstrument,
                $"unknown instrument '{instrumentId}'");

        var openCount = state.OpenOrdersByInstrument[idx].Count;

        // For Replace: net change zero if the prior order is in this instrument's list.
        if (cmd.CommandCase == StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace)
        {
            var existing = false;
            foreach (var o in state.OpenOrdersByInstrument[idx])
            {
                if (o.OrderId == cmd.OrderReplace.OrderId) { existing = true; break; }
            }
            if (existing) return GuardResult.Ok;
        }

        if (openCount + 1 > t.MaxOpenOrdersPerInstrument)
            return GuardResult.Reject(StrategyProto.RejectReason.MaxOpenOrders,
                $"open orders for instrument {instrumentId} = {openCount} (cap {t.MaxOpenOrdersPerInstrument})");
        return GuardResult.Ok;
    }

    private static string ExtractInstrumentId(StrategyProto.StrategyCommand cmd) => cmd.CommandCase switch
    {
        StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit => cmd.OrderSubmit.Instrument?.InstrumentId ?? string.Empty,
        StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace => cmd.OrderReplace.Instrument?.InstrumentId ?? string.Empty,
        _ => string.Empty,
    };
}
