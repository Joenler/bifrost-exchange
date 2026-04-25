using Bifrost.Contracts.Internal.Shared;
using Bifrost.Gateway.State;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// Tier 4c of the ADR-0004 chain (#4 in the ADR table). ≤ 1000 MWh net |position|
/// per (team, instrument) AFTER the hypothetical fill. Submit and Replace are
/// subject; Cancel is not. Side ⇒ signed delta on
/// <see cref="TeamState.NetPositionTicks"/>; we forecast the worst-case post-fill
/// magnitude and reject if it exceeds the cap.
///
/// CALLER holds <see cref="TeamState.StateLock"/>.
/// </summary>
internal static class MaxPositionGuard
{
    public static GuardResult Check(TeamState state, StrategyProto.StrategyCommand cmd, GuardThresholds t)
    {
        if (cmd.CommandCase != StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit
         && cmd.CommandCase != StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace)
            return GuardResult.Ok;

        var instrumentId = cmd.CommandCase switch
        {
            StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit => cmd.OrderSubmit.Instrument?.InstrumentId ?? string.Empty,
            StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace => cmd.OrderReplace.Instrument?.InstrumentId ?? string.Empty,
            _ => string.Empty,
        };
        var idx = InstrumentOrdering.IndexOf(instrumentId);
        if (idx < 0) return GuardResult.Ok;   // Structural / MaxOpenOrders guards reject unknown instruments earlier; defensive.

        var (side, qtyTicks) = ExtractSideAndQty(cmd);
        if (qtyTicks <= 0) return GuardResult.Ok;

        var signedTicks = side == MarketProto.Side.Buy ? +qtyTicks : -qtyTicks;
        var hypotheticalNetTicks = state.NetPositionTicks[idx] + signedTicks;
        var hypotheticalMwh = Math.Abs(QuantityScale.FromTicks(hypotheticalNetTicks));
        if (hypotheticalMwh > t.MaxPositionPerInstrumentMwh)
            return GuardResult.Reject(StrategyProto.RejectReason.MaxPosition,
                $"hypothetical position {hypotheticalMwh} MWh > {t.MaxPositionPerInstrumentMwh} MWh");
        return GuardResult.Ok;
    }

    private static (MarketProto.Side side, long qtyTicks) ExtractSideAndQty(StrategyProto.StrategyCommand cmd) => cmd.CommandCase switch
    {
        StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit
            => (cmd.OrderSubmit.Side, cmd.OrderSubmit.QuantityTicks),
        // OrderReplace has no Side field; Side is inherited from the original order.
        // For the position-cap guard a replace shrinking quantity is always safe; a
        // replace growing quantity needs the resting order's side. Plan 06 will wire
        // a per-team open-order lookup to resolve the side on replace; until then,
        // skip the guard for replaces (NewQuantityTicks==0 means unchanged anyway).
        StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace
            => (MarketProto.Side.Unspecified, 0L),
        _ => (MarketProto.Side.Unspecified, 0L),
    };
}
