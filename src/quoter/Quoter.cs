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

using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Abstractions;
using Bifrost.Quoter.Pricing;
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
    private readonly QuoterConfig _config;
    private readonly ILogger<Quoter> _log;

    // Lock guarding the regime-state machine + the cross-instrument cancel-all path.
    // Must be acquired before any per-instrument lock (see file header).
    private readonly object _globalRegimeLock = new();

    // Future seams (filled in by subsequent plans):
    //   - RegimeSchedule (hybrid scenario beats + Markov overlay).
    //   - RegimeChangePublisher (BufferedEventPublisher binding).
    //   - Channel<QuoterInbound> inbox for MC regime-force commands.

    public Quoter(
        IClock clock,
        TimeProvider timeProvider,
        IRoundStateSource roundState,
        IImbalanceTruthView truth,
        GbmPriceModel gbm,
        PyramidQuoteTracker tracker,
        IOrderContext commandCtx,
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

                // Wave-3 will replace this placeholder with: drain inbox →
                //   schedule.Advance(clock.GetUtcNow()) → transition? → 6-step protocol.
                // For now keep the GBM tick live with a fixed (drift, vol) so the loop
                // exercises the timer + dependency wiring end-to-end.
                _gbm.StepAll(new GbmParams(Drift: 0.0, Vol: 0.02));

                // Per-instrument re-quote is wired in once RegimeSchedule lands; this
                // service is intentionally a thin scaffold this wave.
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
    /// Book-consistency-guarded Replace.
    /// PORTED FROM Arena MarketMaker.cs:694-720 — book-consistency guard for Replace.
    /// 96% of phantom fills would otherwise produce OrderNotFound rejects: skip Replace
    /// when the tracked level is absent from the current book, and let the pending
    /// OrderFill / OrderCancelled lifecycle callback reconcile the tracker state.
    /// </summary>
    private void QuoteSide(InstrumentId instrument, Side side, long targetPrice, OrderBook snapshot)
    {
        // Pseudocode for next-wave wiring:
        //   var sideLevels = snapshot.GetLevels(side);
        //   bool levelPresent = false;
        //   foreach (var bookLevel in sideLevels)
        //       if (bookLevel.Price.Ticks == order.Price.Ticks) { levelPresent = true; break; }
        //   if (!levelPresent) { _commandCtx.Logger.LogDebug("STALE-REPLACE-SUPPRESSED ..."); return; }
        //
        //   if (Math.Abs(order.Price.Ticks - targetPrice) <= _config.RequoteThresholdTicks) return;
        //   if (order.IsReplacePending) return;
        //   _commandCtx.ReplaceOrder(instrument, order.OrderId, targetPrice, null);
        _ = _clock;
        _ = _truth;
        _ = _tracker;
        _ = _commandCtx;
        throw new NotImplementedException(
            "QuoteSide wiring completes once the regime schedule and order-context publisher land.");
    }
}
