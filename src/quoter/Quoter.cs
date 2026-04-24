// LOCK ORDER: _globalRegimeLock → _perInstrumentState[i] (i ∈ [0..4], _sortedInstruments index order).
// Cross-instrument operations (cancel-all-on-transition) acquire ALL per-instrument locks in
// index order. Violating this order risks deadlock vs the re-quote path.
//
// REGIME TRANSITION PROTOCOL (authoritative — do not reorder):
//   1. Compute newRegime, newParams = schedule.CurrentParams()
//   2. EMIT Event.RegimeChange { from, to, mc_forced }    ← FIRST, before cancels
//   3. For each instrument (index order): tracker.CancelAllSides(inst, ctx)
//   4. Record cancel-pending cohort — next tick's re-quote skips in-flight slots
//   5. Compute fair_value = w·truth + (1-w)·microprice  (with empty-book fallback)
//   6. For each instrument, side, level: submit Limit at A-S half-spread
//
// QTR gate: early-return when _roundState.Current != RoundState.RoundOpen.

using System.Threading.Channels;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Abstractions;
using Bifrost.Quoter.Pricing;
using Bifrost.Quoter.Schedule;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.Quoter;

/// <summary>
/// Single-writer quoter <see cref="BackgroundService"/>. Each tick is driven by a
/// <see cref="PeriodicTimer"/> constructed against the injected <see cref="TimeProvider"/>
/// so test runs can drive the loop deterministically via <c>FakeTimeProvider</c>.
/// </summary>
public sealed class Quoter : BackgroundService
{
    private readonly IClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly IRoundStateSource _roundState;
    private readonly IImbalanceTruthView _truth;
    private readonly GbmPriceModel _gbm;
    private readonly PyramidQuoteTracker _tracker;
    private readonly IOrderContext _commandCtx;
    private readonly RegimeSchedule _schedule;
    private readonly Channel<RegimeForceMessage> _inbox;
    private readonly IRegimeChangePublisher _regimeEvents;
    private readonly InstrumentId[] _sortedInstruments;
    private readonly QuoterConfig _config;
    private readonly ILogger<Quoter> _log;

    // Lock guarding the regime-state machine + the cross-instrument cancel-all path.
    // Must be acquired before any per-instrument lock (see file header).
    private readonly object _globalRegimeLock = new();

    public Quoter(
        IClock clock,
        TimeProvider timeProvider,
        IRoundStateSource roundState,
        IImbalanceTruthView truth,
        GbmPriceModel gbm,
        PyramidQuoteTracker tracker,
        IOrderContext commandCtx,
        RegimeSchedule schedule,
        Channel<RegimeForceMessage> inbox,
        IRegimeChangePublisher regimeEvents,
        IEnumerable<InstrumentId> instruments,
        IOptions<QuoterConfig> config,
        ILogger<Quoter> log)
    {
        _clock = clock;
        _timeProvider = timeProvider;
        _roundState = roundState;
        _truth = truth;
        _gbm = gbm;
        _tracker = tracker;
        _commandCtx = commandCtx;
        _schedule = schedule;
        _inbox = inbox;
        _regimeEvents = regimeEvents;
        // Stable iteration order for cross-instrument operations (lock order
        // depends on this being deterministic across ticks).
        _sortedInstruments = instruments
            .OrderBy(i => i.DeliveryArea.Value, StringComparer.Ordinal)
            .ThenBy(i => i.DeliveryPeriod.Start)
            .ToArray();
        _config = config.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.GbmStepMs), _timeProvider);
        _log.LogInformation("Quoter started — PeriodicTimer cadence {Ms} ms", _config.GbmStepMs);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (_roundState.Current != RoundState.RoundOpen)
                    continue;

                // (a) Drain MC-force inbox first so the operator-installed regime
                // is visible to the schedule.Advance below.
                while (_inbox.Reader.TryRead(out var msg))
                {
                    var forced = _schedule.InstallMcForce(msg.Regime, msg.Nonce);
                    if (forced is not null)
                        HandleRegimeTransition(forced.Value);
                }

                // (b) Step the schedule and react to any natural / scheduled transition.
                var sched = _schedule.Advance(_clock.GetUtcNow());
                if (sched is not null)
                    HandleRegimeTransition(sched.Value);

                // (c) Step GBM with the current regime's (drift, vol).
                _gbm.StepAll(_schedule.CurrentGbmParams());

                // (d) Per-instrument re-quote in stable lock-order.
                foreach (var inst in _sortedInstruments)
                    Requote(inst);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Quoter tick failure");
                await Task.Delay(100, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Steps 2 + 3 of the regime transition protocol. Emit FIRST (so observers
    /// see the transition in causal order), then cancel-all in the documented
    /// per-instrument lock order. The next tick's <see cref="Requote"/> calls
    /// repopulate the pyramid against the new regime params.
    /// </summary>
    private void HandleRegimeTransition(RegimeTransition t)
    {
        lock (_globalRegimeLock)
        {
            // Step 2: emit the regime-change event BEFORE issuing any cancels.
            _regimeEvents.Emit(t);

            // Step 3: cancel-all across every instrument in deterministic
            // index order. Steps 4-6 land on the next tick(s) via Requote().
            foreach (var inst in _sortedInstruments)
                _tracker.CancelAllSides(inst, _commandCtx);

            _log.LogInformation(
                "Regime transition: {From} -> {To} (McForced={Mc}, Reason={Reason})",
                t.From, t.To, t.McForced, t.Reason);
        }
    }

    /// <summary>
    /// Steps 5 + 6 of the regime transition protocol on the per-instrument
    /// hot path. Computes fair value (truth blended with microprice when both
    /// sides have non-self quantity) and submits / replaces a 3-level pyramid.
    /// Hard-cap inventory guard suppresses the side that would worsen position;
    /// net position will be supplied by the position feed in a later wave --
    /// for now the guard is invoked with the ticker-clean state (zero net) so
    /// both sides are quoted.
    /// </summary>
    private void Requote(InstrumentId inst)
    {
        var rp = _schedule.CurrentParams();
        var truthTicks = _truth.GetTruePriceTicks(inst);
        var fairValue = ComputeFairValue(inst, truthTicks);

        // Inventory guard: when the position feed is wired, this will be the
        // current net position; until then the quoter is always inside the band.
        var directive = HardCapGuard.Evaluate(
            netPosition: 0m,
            maxNetPosition: _config.MaxNetPosition,
            hardCapRelease: _config.HardCapRelease,
            previous: new InventoryDirective(QuoteBids: true, QuoteAsks: true));

        var halfSpreadCore = AvellanedaStoikov.OptimalHalfSpread(
            inventoryRiskAversion: _config.InventoryRiskAversion,
            orderArrivalIntensity: rp.Kappa);

        var levels = Math.Min(_config.LevelSpacingMultipliers.Length, _config.LevelQuantityFractions.Length);
        var lastBid = long.MinValue;
        var lastAsk = long.MaxValue;

        for (var level = 0; level < levels; level++)
        {
            var halfSpread = rp.SpreadMultiplier * halfSpreadCore * _config.LevelSpacingMultipliers[level];
            var range = AvellanedaStoikov.ComputeQuotableRange(fairValue, halfSpread, tickSize: 1);

            var bid = range.BidTicks;
            var ask = range.AskTicks;

            // Inter-level tick separation: each subsequent level must be at
            // least one tick further from fair value than the prior level on
            // the same side. Snap by 1 tick if collision occurs.
            if (level > 0)
            {
                if (bid >= lastBid) bid = lastBid - 1;
                if (ask <= lastAsk) ask = lastAsk + 1;
            }

            var qty = _config.BaseQuantity * (decimal)(rp.QuantityMultiplier * _config.LevelQuantityFractions[level]);

            if (directive.QuoteBids)
                SubmitOrReplace(inst, Side.Buy, level, bid, qty);
            if (directive.QuoteAsks)
                SubmitOrReplace(inst, Side.Sell, level, ask, qty);

            lastBid = bid;
            lastAsk = ask;
        }
    }

    private long ComputeFairValue(InstrumentId inst, long truthTicks)
    {
        // No BookView wired yet -- microprice is null until the gateway pipes
        // the order book through. The blend collapses to pure truth (w = 1).
        // When the BookView lands, the existing MicropriceCalculator can be
        // invoked here with the tracker's owned-order set as the self-filter.
        return truthTicks;
    }

    private void SubmitOrReplace(InstrumentId inst, Side side, int level, long priceTicks, decimal qty)
    {
        if (_tracker.TryGetTrackedSlot(inst, side, level, out var slot))
        {
            QuoteSide(inst, side, level, priceTicks, slot);
            return;
        }

        // No working order at this slot -- fresh submit (TrackOrder records the
        // correlation id + submit price; OnOrderAccepted will later attach the
        // OrderId). The priceTicks write feeds the jitter guard in QuoteSide
        // on the next tick once the slot promotes from pending to working.
        var corr = _commandCtx.SubmitLimitOrder(inst, side, priceTicks, qty);
        _tracker.TrackOrder(inst, side, level, corr, priceTicks);
    }

    /// <summary>
    /// Book-consistency-guarded Replace. Reads the tracker-owned slot directly
    /// (NEVER <c>IOrderContext.GetOrder</c>) so the tracker remains the single
    /// source of truth for quoter-owned working orders.
    /// </summary>
    private void QuoteSide(
        InstrumentId instrument,
        Side side,
        int level,
        long targetPrice,
        PyramidQuoteTracker.LevelOrder slot)
    {
        // Tracker invariant (TryGetTrackedSlot returned true): OrderId is set.
        if (slot.OrderId is not { } orderId)
            return;

        // Suppress small jitter: stay put if we're already within the requote
        // threshold of target. Tick-distance comparison keeps the threshold
        // configurable without recomputing half-spread.
        if (Math.Abs(slot.PriceTicks - targetPrice) <= _config.RequoteThresholdTicks)
            return;

        // A pending submit at this slot means the previous Replace has not yet
        // been acknowledged; skip to avoid OrderNotFound rejects. The lifecycle
        // callbacks (OnOrderAccepted / OnFill / OnOrderCancelled) reconcile.
        if (_tracker.HasPendingOrder(instrument, side, level))
            return;

        _commandCtx.ReplaceOrder(instrument, orderId, targetPrice, newQty: null);
        // Keep the tracked slot's price in sync with the live wire target so
        // the next tick's jitter guard (line above) reads fresh state. LevelOrder
        // is a reference type (PyramidQuoteTracker.LevelOrder is a sealed class),
        // so the mutation via the reference returned by TryGetTrackedSlot is
        // visible on the next read. Without this write the guard always sees the
        // stale submit price and every subsequent tick fires a Replace.
        slot.PriceTicks = targetPrice;
    }
}
