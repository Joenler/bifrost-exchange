using System.Threading.Channels;
using Bifrost.Exchange.Application.RoundState;
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

    public SimulatorActorLoop(
        Channel<SimulatorMessage> channel,
        SimulatorState state,
        IRandomSource rng,
        IOptions<ImbalanceSimulatorOptions> options,
        IClock clock,
        ILogger<SimulatorActorLoop> log)
    {
        _channel = channel;
        _state = state;
        _rng = rng;
        _options = options;
        _clock = clock;
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
        // Forecast publish path — delegated emission attaches here later.
        // Scaffolding behaviour: expire any pending transient shocks so their
        // contribution is subtracted off A_physical when the window elapses.
        ExpireTransientShocks(tick.TsNs);
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
                break;
            case RoundState.RoundOpen:
                // Round start. Bump the round number and reseed the PRNG with
                // scenario_seed XOR round_number so byte-identical replays hold
                // across runs.
                _state.CurrentRoundNumber = checked(_state.CurrentRoundNumber + 1);
                var seed = _options.Value.ScenarioSeed ^ (long)_state.CurrentRoundNumber;
                _rng.Reseed(seed);
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
