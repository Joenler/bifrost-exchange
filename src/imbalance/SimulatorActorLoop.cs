using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.Imbalance;

/// <summary>
/// Single-writer drain of the simulator's <see cref="Channel{T}"/> of
/// <see cref="SimulatorMessage"/>. Four producer hosted services (fill consumer,
/// shock consumer, forecast timer, round-state bridge) push messages onto the
/// shared bounded channel; this loop is the SOLE mutator of
/// <see cref="SimulatorState"/>. Pattern mirrors
/// <c>BufferedEventPublisher.DrainLoop</c> but with typed pattern-matching
/// rather than <see cref="Func{TResult}"/> invocation.
/// <para>
/// The scaffolding ships minimum-correct state updates — downstream producer
/// and emission logic is layered on top by later wiring without touching the
/// invariants established here: single-writer drain, exhaustive switch,
/// per-message exception isolation, and round-state tracking with PRNG reseed
/// on <see cref="RoundState.RoundOpen"/>.
/// </para>
/// </summary>
public sealed class SimulatorActorLoop : BackgroundService
{
    private readonly Channel<SimulatorMessage> _channel;
    private readonly SimulatorState _state;
    private readonly IRandomSource _rng;
    private readonly IOptions<ImbalanceSimulatorOptions> _options;
    private readonly IClock _clock;
    private readonly ILogger<SimulatorActorLoop> _log;
    private readonly IEventPublisher _publisher;
    private readonly ImbalancePricingEngine _pricing;

    /// <summary>
    /// Timestamp (unix ns) of the most recent RoundOpen transition. Used by
    /// <see cref="HandleForecastTick"/> to compute elapsed-seconds for the
    /// linear noise-decay fraction. Null outside a live round.
    /// </summary>
    private long? _roundOpenTsNs;

    public SimulatorActorLoop(
        Channel<SimulatorMessage> channel,
        SimulatorState state,
        IRandomSource rng,
        IOptions<ImbalanceSimulatorOptions> options,
        IClock clock,
        IEventPublisher publisher,
        ImbalancePricingEngine pricing,
        ILogger<SimulatorActorLoop> log)
    {
        _channel = channel;
        _state = state;
        _rng = rng;
        _options = options;
        _clock = clock;
        _publisher = publisher;
        _pricing = pricing;
        _log = log;

        // Seed the active regime from configuration; a future regime source will
        // update this via RoundStateMessage arms without changing this default.
        _state.CurrentRegime = _options.Value.DefaultRegime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("SimulatorActorLoop started; draining channel.");

        await foreach (var msg in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (msg)
                {
                    case FillMessage fill:
                        HandleFill(fill);
                        break;
                    case ShockMessage shock:
                        HandleShock(shock);
                        break;
                    case ForecastTickMessage tick:
                        HandleForecastTick(tick);
                        break;
                    case RoundStateMessage rs:
                        HandleRoundStateTransition(rs);
                        break;
                    default:
                        _log.LogWarning("Unknown SimulatorMessage type: {Type}", msg.GetType().Name);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Simulator drain failure on {Type}", msg.GetType().Name);
            }
        }
    }

    // ---- Stub handlers — delegated behaviour attaches in later wiring passes. ----

    private void HandleFill(FillMessage fill)
    {
        // Fill-consumer wiring: update (clientId, quarter_index) → net_position_ticks
        // only while RoundState == RoundOpen. Fills outside RoundOpen are ignored
        // defensively — the exchange should not be matching, but a dropped guard
        // here would corrupt settlement silently.
        if (_state.CurrentRoundState != RoundState.RoundOpen)
        {
            return;
        }

        var key = (fill.ClientId, fill.QuarterIndex);
        _state.NetPositions.TryGetValue(key, out var current);
        _state.NetPositions[key] = current + fill.QuantityTicks;
    }

    private void HandleShock(ShockMessage shock)
    {
        // Shock-consumer wiring.
        // Defense-in-depth: the orchestrator should reject any shock with an
        // unset quarter_index at its boundary; catch regressions here so they
        // surface in tests rather than silently corrupting A_physical.
        System.Diagnostics.Debug.Assert(shock.QuarterIndex is >= 0 and <= 3,
            $"PhysicalShock with invalid quarter_index={shock.QuarterIndex}; upstream contract violation");

        if (shock.QuarterIndex is < 0 or > 3)
        {
            _log.LogError(
                "Dropping PhysicalShock with invalid quarter_index {Qh} — upstream contract violation",
                shock.QuarterIndex);
            return;
        }

        // Only accumulate during RoundOpen — shocks between rounds are dropped.
        if (_state.CurrentRoundState != RoundState.RoundOpen)
        {
            return;
        }

        var contributionTicks = (long)shock.Mw * _options.Value.TicksPerEuro;

        if (shock.Persistence == ShockPersistence.Round)
        {
            _state.APhysicalQh[shock.QuarterIndex] = checked(
                _state.APhysicalQh[shock.QuarterIndex] + contributionTicks);
        }
        else // Transient
        {
            var windowNs = (long)_options.Value.TTransientSeconds * 1_000_000_000L;
            _state.APhysicalQh[shock.QuarterIndex] = checked(
                _state.APhysicalQh[shock.QuarterIndex] + contributionTicks);
            _state.PendingTransients.Add(
                new TransientShock(shock.TsNs, windowNs, shock.QuarterIndex, contributionTicks));
        }
    }

    private void HandleForecastTick(ForecastTickMessage tick)
    {
        // Expire transient shocks first — this is a state-only cleanup that runs
        // on every tick regardless of RoundState. A transient shock still inside
        // its window on the last tick of RoundOpen should roll off naturally
        // even if no forecast is subsequently emitted.
        ExpireTransientShocks(tick.TsNs);

        // Gate publication on RoundOpen. Forecasts are emitted ONLY during a
        // live round per the public-forecast invariant: zero forecasts during
        // IterationOpen / AuctionOpen / AuctionClosed / Gate / Settled /
        // Aborted. The timer itself fires on every cadence tick regardless of
        // state; the gate is here in the drain loop where CurrentRoundState is
        // the single-writer truth.
        if (_state.CurrentRoundState != RoundState.RoundOpen)
        {
            return;
        }

        // _roundOpenTsNs is set on the RoundOpen arm of HandleRoundStateTransition.
        // If it's null in RoundOpen something invariant-breaking happened; log and
        // skip this tick rather than corrupt the decay math.
        if (_roundOpenTsNs is null)
        {
            _log.LogWarning(
                "ForecastTick while CurrentRoundState=RoundOpen but _roundOpenTsNs is unset; skipping");
            return;
        }

        // Aggregate team net-positions per QH, excluding non-settlement clients
        // (quoter, dah-auction). The forecast is a single scalar averaged across
        // the four quarters — teams see one public prediction that reflects the
        // round-wide aggregate imbalance. Per-QH fan-out happens at Gate via
        // ImbalancePrint; per-team jitter is Phase 07 Gateway's responsibility.
        var deny = _options.Value.NonSettlementClientIds ?? Array.Empty<string>();
        var aTeamsByQh = new long[4];
        foreach (var (key, pos) in _state.NetPositions)
        {
            if (Array.IndexOf(deny, key.ClientId) >= 0)
            {
                continue;
            }
            if (key.QuarterIndex is < 0 or > 3)
            {
                continue;
            }
            aTeamsByQh[key.QuarterIndex] = checked(aTeamsByQh[key.QuarterIndex] + pos);
        }

        var elapsedSeconds = (tick.TsNs - _roundOpenTsNs.Value) / 1_000_000_000.0;
        if (elapsedSeconds < 0.0)
        {
            elapsedSeconds = 0.0;
        }

        // Compute a per-QH forecast price and average across the four quarters
        // as a single scalar for the public wire. Each call consumes one draw
        // from the PRNG so replays are byte-deterministic.
        long totalForecastTicks = 0L;
        for (var qh = 0; qh < 4; qh++)
        {
            totalForecastTicks = checked(
                totalForecastTicks
                + _pricing.ComputeForecastPriceTicks(
                    elapsedSecondsSinceRoundOpen: elapsedSeconds,
                    activeQuarterIndex: qh,
                    aTeamsTicks: aTeamsByQh[qh],
                    aPhysicalTicks: _state.APhysicalQh[qh],
                    regime: _state.CurrentRegime,
                    rng: _rng));
        }
        var avgForecastTicks = totalForecastTicks / 4L;

        // HorizonNs = remaining time until Gate; clamped at zero if the tick
        // fires past the nominal round end (PeriodicTimer cannot fire after
        // Settled in practice — the drain loop's state gate above rejects —
        // but clamp for defence).
        var roundDurationNs = (long)_options.Value.RoundDurationSeconds * 1_000_000_000L;
        var elapsedNs = tick.TsNs - _roundOpenTsNs.Value;
        var horizonNs = Math.Max(0L, roundDurationNs - elapsedNs);

        var payload = new ForecastUpdateEvent(
            ForecastPriceTicks: avgForecastTicks,
            HorizonNs: horizonNs,
            TimestampNs: tick.TsNs);

        _publisher.PublishPublicEvent(
            RabbitMqTopology.PublicForecastRoutingKey,
            MessageTypes.ForecastUpdate,
            payload);
    }

    private void HandleRoundStateTransition(RoundStateMessage rs)
    {
        _log.LogInformation("RoundState {Previous} -> {Current}", rs.Previous, rs.Current);
        _state.CurrentRoundState = rs.Current;

        switch (rs.Current)
        {
            case RoundState.IterationOpen:
                // Reload + round clear — delegated handler. Scaffolding behaviour
                // is to reset all round-scoped accumulators ahead of the next
                // round's accumulation.
                _state.ResetForNewRound();
                _roundOpenTsNs = null;
                break;
            case RoundState.RoundOpen:
                // Round start. Bump the round number and reseed the PRNG with
                // scenario_seed XOR round_number so byte-identical replays hold
                // across runs. Stamp the round-open timestamp so the forecast
                // tick handler can compute elapsed-seconds for linear noise
                // decay against a single consistent anchor.
                _state.CurrentRoundNumber = checked(_state.CurrentRoundNumber + 1);
                var seed = _options.Value.ScenarioSeed ^ (long)_state.CurrentRoundNumber;
                _rng.Reseed(seed);
                _roundOpenTsNs = rs.TsNs;
                _log.LogInformation(
                    "RoundOpen: round={Round} reseeded rng with {Seed}",
                    _state.CurrentRoundNumber, seed);
                break;
            case RoundState.Gate:
                // Gate emits 4 ImbalancePrint (one per quarter) — delegated handler.
                break;
            case RoundState.Settled:
                // Settled emits N×4 ImbalanceSettlement rows — delegated handler.
                break;
            case RoundState.AuctionOpen:
            case RoundState.AuctionClosed:
            case RoundState.Aborted:
                // No per-state behaviour in the scaffolding scope.
                break;
        }
    }

    private void ExpireTransientShocks(long nowTsNs)
    {
        // Iterate + remove any transient whose window has elapsed; subtract its
        // contribution from A_physical on rolloff.
        for (var i = _state.PendingTransients.Count - 1; i >= 0; i--)
        {
            var t = _state.PendingTransients[i];
            if (nowTsNs - t.ActivatedTsNs >= t.TransientWindowNs)
            {
                _state.APhysicalQh[t.QuarterIndex] = checked(
                    _state.APhysicalQh[t.QuarterIndex] - t.ContributionTicks);
                _state.PendingTransients.RemoveAt(i);
            }
        }
    }
}
