using Bifrost.Gateway.State;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// Tier 5 of the ADR-0004 chain (#5 in the ADR table). Cancel-newer when a team's
/// new buy would cross its own resting sell on the same instrument (or new sell
/// crosses own resting buy). The newer order is rejected; older resting orders
/// are preserved per ADR-0004 cancel-newer semantics.
///
/// CALLER holds <see cref="TeamState.StateLock"/>.
/// </summary>
internal static class SelfTradeGuard
{
    public static GuardResult Check(TeamState state, StrategyProto.StrategyCommand cmd)
    {
        // OrderReplace has no Side or PriceTicks shape on its own — a replace of an
        // existing order's price/qty against the same side cannot create a new
        // self-cross beyond what was already validated when the original was accepted.
        // Plan 06 will revisit if a per-instrument resting-side lookup is needed for
        // replaces; until then, only Submit is checked.
        if (cmd.CommandCase != StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit)
            return GuardResult.Ok;

        var p = cmd.OrderSubmit;
        var instrumentId = p.Instrument?.InstrumentId ?? string.Empty;
        var idx = InstrumentOrdering.IndexOf(instrumentId);
        if (idx < 0) return GuardResult.Ok;   // Other guards reject unknown instruments.

        var newSide = p.Side;
        var newPriceTicks = p.PriceTicks;
        // For market orders priceTicks is 0; treat as crossing only if the team has any
        // resting orders on the opposite side (a market order will sweep them).
        var open = state.OpenOrdersByInstrument[idx];

        bool wouldCross;
        if (newSide == MarketProto.Side.Buy)
        {
            // New buy crosses own resting sells priced ≤ new buy price.
            // Market buy (priceTicks == 0) is treated as marketable against any resting sell.
            wouldCross = false;
            foreach (var o in open)
            {
                if (o.Side == "Sell" && (newPriceTicks == 0 || newPriceTicks >= o.PriceTicks))
                {
                    wouldCross = true; break;
                }
            }
        }
        else if (newSide == MarketProto.Side.Sell)
        {
            // New sell crosses own resting buys priced ≥ new sell price.
            wouldCross = false;
            foreach (var o in open)
            {
                if (o.Side == "Buy" && (newPriceTicks == 0 || newPriceTicks <= o.PriceTicks))
                {
                    wouldCross = true; break;
                }
            }
        }
        else
        {
            return GuardResult.Ok;
        }

        if (wouldCross)
            return GuardResult.Reject(StrategyProto.RejectReason.SelfTrade,
                $"new {newSide} at {newPriceTicks} crosses own resting orders on {instrumentId}");
        return GuardResult.Ok;
    }
}
