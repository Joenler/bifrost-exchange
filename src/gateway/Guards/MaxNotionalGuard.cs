using Bifrost.Contracts.Internal.Shared;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// Tier 4a of the ADR-0004 chain (#3 in the ADR table). Per-order notional cap
/// in MWh. Default 50 MWh. Submit uses <see cref="StrategyProto.OrderSubmit.QuantityTicks"/>;
/// Replace uses <see cref="StrategyProto.OrderReplace.NewQuantityTicks"/> (0 = unchanged
/// → guard skips, since the existing order's notional was already validated).
/// </summary>
public static class MaxNotionalGuard
{
    public static GuardResult Check(StrategyProto.StrategyCommand cmd, GuardThresholds t)
    {
        long qtyTicks = cmd.CommandCase switch
        {
            StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit => cmd.OrderSubmit.QuantityTicks,
            // OrderReplace.NewQuantityTicks: 0 means "leave unchanged" (strategy.proto:54);
            // skip the guard in that case — the live notional was validated when the
            // original order was accepted.
            StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace => cmd.OrderReplace.NewQuantityTicks,
            _ => 0,
        };
        if (qtyTicks <= 0) return GuardResult.Ok;

        // QuantityScale.FromTicks → decimal MWh. Threshold is int MWh; compare in decimal.
        var mwh = QuantityScale.FromTicks(qtyTicks);
        if (mwh > t.MaxOrderNotionalMwh)
            return GuardResult.Reject(StrategyProto.RejectReason.MaxNotional,
                $"order notional {mwh} MWh > {t.MaxOrderNotionalMwh} MWh");
        return GuardResult.Ok;
    }
}
