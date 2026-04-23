using System.Collections.Concurrent;
using System.Collections.Immutable;
using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Abstractions;
// Disambiguate the four Bifrost.Quoter.Pricing.Events.* records (correlation-id-bearing
// quoter-side reconciliation events co-donated from Arena) from the matching-engine-internal
// Bifrost.Exchange.Domain.{OrderAccepted, OrderRejected, OrderCancelled} records (Phase 02 Domain).
// Without these aliases the simple-name references in OnOrderAccepted / OnFill / OnOrderCancelled /
// OnOrderRejected method signatures and bodies are ambiguous (CS0104).
using OrderAccepted = Bifrost.Quoter.Pricing.Events.OrderAccepted;
using OrderFill = Bifrost.Quoter.Pricing.Events.OrderFill;
using OrderCancelled = Bifrost.Quoter.Pricing.Events.OrderCancelled;
using OrderRejected = Bifrost.Quoter.Pricing.Events.OrderRejected;

namespace Bifrost.Quoter.Pricing;

public sealed class PyramidQuoteTracker
{
    private volatile ImmutableDictionary<InstrumentId, LevelSet> _entries = ImmutableDictionary<InstrumentId, LevelSet>.Empty;
    private readonly ConcurrentDictionary<CorrelationId, (InstrumentId Inst, Side Side, int Level)> _pending = new();
    private volatile ImmutableDictionary<CorrelationId, DateTimeOffset> _pendingTimestamps = ImmutableDictionary<CorrelationId, DateTimeOffset>.Empty;
    private readonly ConcurrentDictionary<OrderId, (InstrumentId Inst, Side Side, int Level)> _accepted = new();
    private readonly int _maxLevels;
    private readonly TimeProvider _timeProvider;

    public sealed class LevelOrder
    {
        public OrderId? OrderId;
        public CorrelationId? CorrelationId;
        public long PriceTicks;
    }

    public sealed class LevelSet
    {
        public readonly LevelOrder[] BuyLevels;
        public readonly LevelOrder[] SellLevels;

        public LevelSet(int maxLevels)
        {
            BuyLevels = new LevelOrder[maxLevels];
            SellLevels = new LevelOrder[maxLevels];
            for (var i = 0; i < maxLevels; i++)
            {
                BuyLevels[i] = new LevelOrder();
                SellLevels[i] = new LevelOrder();
            }
        }
    }

    public PyramidQuoteTracker(int maxLevels, TimeProvider? timeProvider = null)
    {
        if (maxLevels < 1 || maxLevels > 5)
            throw new ArgumentOutOfRangeException(nameof(maxLevels), maxLevels, "Must be between 1 and 5.");
        _maxLevels = maxLevels;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public LevelSet GetOrCreate(InstrumentId instrument)
    {
        var current = _entries;
        if (current.TryGetValue(instrument, out var existing))
            return existing;

        var newEntry = new LevelSet(_maxLevels);
        ImmutableDictionary<InstrumentId, LevelSet> snapshot, updated;
        do
        {
            snapshot = _entries;
            if (snapshot.TryGetValue(instrument, out existing))
                return existing;
            updated = snapshot.SetItem(instrument, newEntry);
        } while (Interlocked.CompareExchange(ref _entries, updated, snapshot) != snapshot);
        return newEntry;
    }

    public void TrackOrder(InstrumentId instrument, Side side, int level, CorrelationId correlationId)
    {
        var entry = GetOrCreate(instrument);
        var levels = side == Side.Buy ? entry.BuyLevels : entry.SellLevels;
        levels[level].CorrelationId = correlationId;
        levels[level].OrderId = null;
        _pending[correlationId] = (instrument, side, level);

        ImmutableDictionary<CorrelationId, DateTimeOffset> tsSnap, tsUpd;
        do
        {
            tsSnap = _pendingTimestamps;
            tsUpd = tsSnap.SetItem(correlationId, _timeProvider.GetUtcNow());
        } while (Interlocked.CompareExchange(ref _pendingTimestamps, tsUpd, tsSnap) != tsSnap);
    }

    public bool OnOrderAccepted(OrderAccepted accepted)
    {
        if (accepted.CorrelationId is not { } correlationId)
            return false;

        if (!_pending.TryGetValue(correlationId, out var loc))
            return false;

        var entries = _entries;
        if (!entries.TryGetValue(loc.Inst, out var entry))
            return false;

        var levels = loc.Side == Side.Buy ? entry.BuyLevels : entry.SellLevels;
        if (levels[loc.Level].CorrelationId == correlationId)
        {
            levels[loc.Level].OrderId = accepted.OrderId;
            _pending.TryRemove(correlationId, out _);
            RemovePendingTimestamp(correlationId);
            _accepted[accepted.OrderId] = loc;
            return true;
        }

        return false;
    }

    public bool OnFill(OrderFill fill)
    {
        if (!_accepted.TryGetValue(fill.OrderId, out var loc))
            return false;

        if (fill.RemainingQuantity == 0m)
        {
            ClearLevel(loc.Inst, loc.Side, loc.Level);
            _accepted.TryRemove(fill.OrderId, out _);
        }

        return true;
    }

    public bool OnOrderCancelled(OrderCancelled cancelled)
    {
        if (_accepted.TryGetValue(cancelled.OrderId, out var loc))
        {
            ClearLevel(loc.Inst, loc.Side, loc.Level);
            _accepted.TryRemove(cancelled.OrderId, out _);
            return true;
        }

        return false;
    }

    public bool OnOrderRejected(OrderRejected rejected)
    {
        if (rejected.CorrelationId is { } correlationId &&
            _pending.TryGetValue(correlationId, out var loc))
        {
            ClearLevel(loc.Inst, loc.Side, loc.Level);
            _pending.TryRemove(correlationId, out _);
            RemovePendingTimestamp(correlationId);
            return true;
        }

        if (rejected.OrderId != default && _accepted.TryGetValue(rejected.OrderId, out var acceptedLoc))
        {
            ClearLevel(acceptedLoc.Inst, acceptedLoc.Side, acceptedLoc.Level);
            _accepted.TryRemove(rejected.OrderId, out _);
            return true;
        }

        return false;
    }

    public bool HasPendingOrder(InstrumentId instrument, Side side, int level)
    {
        var entries = _entries;
        if (!entries.TryGetValue(instrument, out var entry))
            return false;

        var levels = side == Side.Buy ? entry.BuyLevels : entry.SellLevels;
        return levels[level].CorrelationId is not null && levels[level].OrderId is null;
    }

    public OrderId? GetOrderId(InstrumentId instrument, Side side, int level)
    {
        var entries = _entries;
        if (!entries.TryGetValue(instrument, out var entry))
            return null;

        var levels = side == Side.Buy ? entry.BuyLevels : entry.SellLevels;
        return levels[level].OrderId;
    }

    public void CancelAll(InstrumentId instrument, Side side, IOrderContext ctx)
    {
        var entries = _entries;
        if (!entries.TryGetValue(instrument, out var entry))
            return;

        var levels = side == Side.Buy ? entry.BuyLevels : entry.SellLevels;
        for (var i = 0; i < _maxLevels; i++)
        {
            if (levels[i].OrderId is { } oid)
                ctx.CancelOrder(instrument, oid);
        }
    }

    public void CancelAllSides(InstrumentId instrument, IOrderContext ctx)
    {
        CancelAll(instrument, Side.Buy, ctx);
        CancelAll(instrument, Side.Sell, ctx);
    }

    public IEnumerable<OrderId> GetAllOrderIds(InstrumentId instrument)
    {
        var entries = _entries;
        if (!entries.TryGetValue(instrument, out var entry))
            yield break;

        for (var i = 0; i < _maxLevels; i++)
        {
            if (entry.BuyLevels[i].OrderId is { } buyOid)
                yield return buyOid;
            if (entry.SellLevels[i].OrderId is { } sellOid)
                yield return sellOid;
        }
    }

    public (int Working, int Pending, int Empty) GetSlotSummary()
    {
        int working = 0, pending = 0, empty = 0;
        var entries = _entries;
        foreach (var (_, entry) in entries)
        {
            for (var i = 0; i < _maxLevels; i++)
            {
                CountSlot(entry.BuyLevels[i], ref working, ref pending, ref empty);
                CountSlot(entry.SellLevels[i], ref working, ref pending, ref empty);
            }
        }
        return (working, pending, empty);

        static void CountSlot(LevelOrder slot, ref int w, ref int p, ref int e)
        {
            if (slot.OrderId is not null) w++;
            else if (slot.CorrelationId is not null) p++;
            else e++;
        }
    }

    public bool HasEmptySlots(InstrumentId instrument)
    {
        var entries = _entries;
        if (!entries.TryGetValue(instrument, out var entry))
            return true;

        for (var i = 0; i < _maxLevels; i++)
        {
            if (entry.BuyLevels[i].OrderId is null && entry.BuyLevels[i].CorrelationId is null)
                return true;
            if (entry.SellLevels[i].OrderId is null && entry.SellLevels[i].CorrelationId is null)
                return true;
        }

        return false;
    }

    public int ClearStalePending(TimeSpan timeout)
    {
        var cutoff = _timeProvider.GetUtcNow() - timeout;
        var cleared = 0;
        var timestamps = _pendingTimestamps;
        foreach (var (corrId, timestamp) in timestamps)
        {
            if (timestamp > cutoff) continue;
            if (_pending.TryRemove(corrId, out var loc))
            {
                ClearLevel(loc.Inst, loc.Side, loc.Level);
                cleared++;
            }
            RemovePendingTimestamp(corrId);
        }
        return cleared;
    }

    private void RemovePendingTimestamp(CorrelationId correlationId)
    {
        ImmutableDictionary<CorrelationId, DateTimeOffset> tsSnap, tsUpd;
        do
        {
            tsSnap = _pendingTimestamps;
            tsUpd = tsSnap.Remove(correlationId);
        } while (Interlocked.CompareExchange(ref _pendingTimestamps, tsUpd, tsSnap) != tsSnap);
    }

    private void ClearLevel(InstrumentId instrument, Side side, int level)
    {
        var entries = _entries;
        if (!entries.TryGetValue(instrument, out var entry))
            return;

        var levels = side == Side.Buy ? entry.BuyLevels : entry.SellLevels;
        levels[level].OrderId = null;
        levels[level].CorrelationId = null;
        levels[level].PriceTicks = 0;
    }
}
