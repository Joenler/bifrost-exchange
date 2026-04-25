using Bifrost.Contracts.Internal;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Translation;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Position;

/// <summary>
/// Sole authority for per-team per-instrument position (GW-06; D-06a). On every
/// Fill, updates (NetPositionTicks, VwapTicks) for the affected instrument and
/// returns a <see cref="StrategyProto.MarketEvent"/> wrapping the resulting
/// <see cref="StrategyProto.PositionSnapshot"/> the caller MUST enqueue
/// IMMEDIATELY after the originating Fill envelope. Order-bookkeeping
/// (OpenOrdersByInstrument + OpenOrdersNotionalTicks) is updated on
/// Accepted / Cancelled / Replaced.
///
/// CALLER INVARIANT: every public method runs UNDER teamState.StateLock. The
/// returned MarketEvent is meant to be written OUTSIDE the lock (Pitfall 10).
/// No <see cref="DateTime.UtcNow"/>, no <see cref="Random.Shared"/>, no
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class PositionTracker
{
    /// <summary>
    /// CALLER holds <see cref="TeamState.StateLock"/>. Returns the snapshot
    /// MarketEvent the caller enqueues after the originating Fill envelope.
    /// VWAP edge cases:
    ///   - Position grows from zero or flips through zero: VWAP = fillPrice.
    ///   - Position grows in same direction:               VWAP = (|old|*oldVwap + filled*fillPrice) / |new|.
    ///   - Position shrinks (partial closeout):            VWAP unchanged.
    /// </summary>
    public StrategyProto.MarketEvent OnFill(
        TeamState state,
        InstrumentIdDto instrumentDto,
        string instrumentIdString,
        MarketProto.ProductType productType,
        MarketProto.Side side,
        long filledQtyTicks,
        long fillPriceTicks,
        long sequence = 0L,
        long timestampNs = 0L)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(instrumentDto);
        ArgumentException.ThrowIfNullOrEmpty(instrumentIdString);
        if (side != MarketProto.Side.Buy && side != MarketProto.Side.Sell)
            throw new ArgumentException($"Side must be Buy or Sell, got {side}", nameof(side));
        if (filledQtyTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(filledQtyTicks), "must be > 0");

        var idx = InstrumentOrdering.IndexOf(instrumentIdString);
        if (idx < 0) throw new ArgumentException($"unknown instrument {instrumentIdString}", nameof(instrumentIdString));

        var signed = side == MarketProto.Side.Buy ? +filledQtyTicks : -filledQtyTicks;
        var oldNet = state.NetPositionTicks[idx];
        var newNet = oldNet + signed;

        long newVwap;
        if (oldNet == 0)
        {
            // Opening a fresh position from flat.
            newVwap = newNet == 0 ? 0 : fillPriceTicks;
        }
        else if (newNet == 0)
        {
            // Closed flat — VWAP becomes meaningless; reset to 0 for clarity.
            newVwap = 0;
        }
        else if (Math.Sign(oldNet) != Math.Sign(newNet))
        {
            // Flipped through zero — VWAP resets to fill price for the new direction.
            newVwap = fillPriceTicks;
        }
        else if (Math.Abs(newNet) > Math.Abs(oldNet))
        {
            // Growing in same direction → weighted running mean.
            var oldAbs = Math.Abs(oldNet);
            var newAbs = Math.Abs(newNet);
            newVwap = ((oldAbs * state.VwapTicks[idx]) + (filledQtyTicks * fillPriceTicks)) / newAbs;
        }
        else
        {
            // Shrinking in same direction (partial closeout) — VWAP unchanged.
            newVwap = state.VwapTicks[idx];
        }

        state.NetPositionTicks[idx] = newNet;
        state.VwapTicks[idx] = newVwap;

        return OutboundTranslator.BuildPositionSnapshot(
            instrumentId: instrumentDto,
            instrumentIdString: instrumentIdString,
            productType: productType,
            netPositionTicks: newNet,
            averagePriceTicks: newVwap,
            openOrdersNotionalTicks: state.OpenOrdersNotionalTicks[idx],
            sequence: sequence,
            timestampNs: timestampNs);
    }

    /// <summary>
    /// CALLER holds <see cref="TeamState.StateLock"/>. Adds the order to
    /// the per-instrument open-order map; bumps OpenOrdersNotionalTicks by
    /// (qty * price). Notional is the absolute value of the live exposure.
    /// </summary>
    public void OnOrderAccepted(TeamState state, OpenOrder record)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(record);
        var idx = record.InstrumentIndex;
        if (idx < 0 || idx >= InstrumentOrdering.Slots)
            throw new ArgumentOutOfRangeException(nameof(record), $"instrumentIndex {idx} out of range");
        state.OpenOrdersByInstrument[idx].Add(record);
        state.OpenOrdersNotionalTicks[idx] += record.QuantityTicks * record.PriceTicks;
    }

    /// <summary>
    /// CALLER holds <see cref="TeamState.StateLock"/>. Removes the matching
    /// resting order (by OrderId — the wire identity for OrderCancel /
    /// OrderExecuted), and decrements notional by its (qty * price). If no
    /// matching record exists, the call is a no-op (the order may have already
    /// been filled or cancelled out-of-band).
    /// </summary>
    public void OnOrderCancelled(TeamState state, int instrumentIndex, long orderId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (instrumentIndex < 0 || instrumentIndex >= InstrumentOrdering.Slots)
            throw new ArgumentOutOfRangeException(nameof(instrumentIndex));
        var open = state.OpenOrdersByInstrument[instrumentIndex];
        for (var i = 0; i < open.Count; i++)
        {
            if (open[i].OrderId == orderId)
            {
                state.OpenOrdersNotionalTicks[instrumentIndex] -= open[i].QuantityTicks * open[i].PriceTicks;
                open.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>
    /// CALLER holds <see cref="TeamState.StateLock"/>. Replace = cancel-by-id +
    /// accept-new under the same lock so the notional swap is atomic per the
    /// open-order map.
    /// </summary>
    public void OnOrderReplaced(TeamState state, int instrumentIndex, long oldOrderId, OpenOrder newRecord)
    {
        ArgumentNullException.ThrowIfNull(newRecord);
        OnOrderCancelled(state, instrumentIndex, oldOrderId);
        OnOrderAccepted(state, newRecord);
    }

    /// <summary>
    /// CALLER holds <see cref="TeamState.StateLock"/>. Decrements
    /// OpenOrdersNotionalTicks on the resting order that produced this fill.
    /// If the fill drains the order completely, the record is removed; if it's
    /// a partial fill, only the consumed portion is debited from notional.
    /// </summary>
    public void OnPartialOrFullFill(TeamState state, int instrumentIndex, long orderId, long filledQtyTicks)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (instrumentIndex < 0 || instrumentIndex >= InstrumentOrdering.Slots)
            throw new ArgumentOutOfRangeException(nameof(instrumentIndex));
        if (filledQtyTicks <= 0) return;
        var open = state.OpenOrdersByInstrument[instrumentIndex];
        for (var i = 0; i < open.Count; i++)
        {
            if (open[i].OrderId == orderId)
            {
                var rec = open[i];
                var consumed = Math.Min(filledQtyTicks, rec.QuantityTicks);
                state.OpenOrdersNotionalTicks[instrumentIndex] -= consumed * rec.PriceTicks;
                var remaining = rec.QuantityTicks - consumed;
                if (remaining <= 0)
                {
                    open.RemoveAt(i);
                }
                else
                {
                    open[i] = rec with { QuantityTicks = remaining };
                }
                return;
            }
        }
    }

    /// <summary>
    /// CALLER holds <see cref="TeamState.StateLock"/>. Used at RegisterAck to
    /// emit the canonical 5-instrument burst (D-06a). The Plan 05 bidi handler
    /// already builds the burst inline from raw arrays — this helper exists
    /// so future callers (load harness, replay tooling) can produce the same
    /// shape from a single seam.
    /// </summary>
    public StrategyProto.MarketEvent[] SnapshotAll(
        TeamState state,
        Func<string, InstrumentIdDto> dtoFactory,
        Func<string, MarketProto.ProductType> productTypeFactory)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(dtoFactory);
        ArgumentNullException.ThrowIfNull(productTypeFactory);
        var result = new StrategyProto.MarketEvent[InstrumentOrdering.Slots];
        for (var i = 0; i < InstrumentOrdering.Slots; i++)
        {
            var id = InstrumentOrdering.CanonicalIds[i];
            result[i] = OutboundTranslator.BuildPositionSnapshot(
                instrumentId: dtoFactory(id),
                instrumentIdString: id,
                productType: productTypeFactory(id),
                netPositionTicks: state.NetPositionTicks[i],
                averagePriceTicks: state.VwapTicks[i],
                openOrdersNotionalTicks: state.OpenOrdersNotionalTicks[i]);
        }
        return result;
    }
}
