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
                HandleGate(rs.TsNs);
                break;
            case RoundState.Settled:
                HandleSettled(rs.TsNs);
                break;
            case RoundState.AuctionOpen:
            case RoundState.AuctionClosed:
            case RoundState.Aborted:
                // No per-state behaviour in the scaffolding scope.
                break;
        }
    }

    /// <summary>
    /// Synthetic-hour anchor for the Phase 04 canonical quarter instruments. Mirrors
    /// the literal in <c>TradingCalendar.GenerateInstruments()</c> — the physical
    /// 9999-01-01 start keeps delivery-period expiry far from any plausible test clock
    /// and is the single reference that Phase 02 and Phase 04 share.
    /// </summary>
    private static readonly DateTimeOffset SyntheticHourStart =
        new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string DeliveryAreaDe = "DE";

    /// <summary>
    /// Realize the four imbalance prints at Gate. For each QH 0..3: aggregate the
    /// non-deny-list teams' net positions, compute P_imb via ADR-0003's pricing engine,
    /// retain the prices in <see cref="SimulatorState.LastPImbTicksPerQuarter"/> for
    /// the subsequent Settled arm, then publish one <see cref="ImbalancePrintEvent"/>
    /// per QH on <c>public.imbalance.print.&lt;instrument_id&gt;</c>. The hour instrument
    /// never appears here — Q0..Q3 only.
    /// </summary>
    private void HandleGate(long tsNs)
    {
        _log.LogInformation(
            "Gate: computing imbalance prices for round {Round}",
            _state.CurrentRoundNumber);

        var deny = _options.Value.NonSettlementClientIds ?? Array.Empty<string>();

        // 1) Aggregate A_teams per QH across all non-deny-list clients. Quoter and
        //    dah-auction are filtered here (T-04-24) so the realized price reflects
        //    only the real-team aggregate.
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

        // 2) Compute P_imb per QH via the pricing engine. Exactly four calls; each
        //    consumes one Gaussian draw from the PRNG so byte-identical replays hold.
        var pimbByQh = new long[4];
        for (var qh = 0; qh < 4; qh++)
        {
            pimbByQh[qh] = _pricing.ComputePImbTicks(
                quarterIndex: qh,
                aTeamsTicks: aTeamsByQh[qh],
                aPhysicalTicks: _state.APhysicalQh[qh],
                regime: _state.CurrentRegime,
                rng: _rng);
        }

        // 3) Retain for Settled. Settled cannot emit without these — a Settled arriving
        //    after a skipped Gate (defensive scenario) is logged and drops the settle.
        _state.LastPImbTicksPerQuarter = pimbByQh;

        // 4) Emit 4 public ImbalancePrint messages — one per QH in instrument order.
        for (var qh = 0; qh < 4; qh++)
        {
            var instrumentDto = BuildQuarterInstrumentDto(qh);
            var instrumentRoutingKey = BuildQuarterInstrumentRoutingKey(qh);
            var aTotalTicks = checked(aTeamsByQh[qh] + _state.APhysicalQh[qh]);

            var print = new ImbalancePrintEvent(
                RoundNumber: _state.CurrentRoundNumber,
                InstrumentId: instrumentDto,
                QuarterIndex: qh,
                PImbTicks: pimbByQh[qh],
                ATotalTicks: aTotalTicks,
                APhysicalTicks: _state.APhysicalQh[qh],
                Regime: _state.CurrentRegime,
                TimestampNs: tsNs);

            _publisher.PublishPublicEvent(
                RabbitMqTopology.PublicImbalancePrintRoutingKey(instrumentRoutingKey),
                MessageTypes.ImbalancePrint,
                print);
        }
    }

    /// <summary>
    /// Realize per-team settlements at Settled. For each distinct non-deny-list client
    /// with any net-position entry: emit one <see cref="ImbalanceSettlementEvent"/> per
    /// QH 0..3 (four rows per team, even if the position for that QH is zero — the
    /// scoring model expects a full per-team matrix). <c>imbalance_pnl_ticks</c> is
    /// computed as <c>checked(position_ticks * p_imb_ticks)</c> — no decimal math, no
    /// rounding (T-04-23). Clients are iterated in ordinal order so emission order is
    /// deterministic across replays.
    /// </summary>
    private void HandleSettled(long tsNs)
    {
        if (_state.LastPImbTicksPerQuarter is null)
        {
            _log.LogError(
                "Settled reached without prior Gate computation in round {Round} — dropping settlement emission",
                _state.CurrentRoundNumber);
            return;
        }

        var deny = _options.Value.NonSettlementClientIds ?? Array.Empty<string>();

        // Enumerate distinct client ids with any net-position entry (including zeros
        // for per-QH slots without a recorded fill). Deterministic ordinal sort so
        // byte-identical replays produce the same emission order.
        var realTeams = _state.NetPositions
            .Select(kv => kv.Key.ClientId)
            .Where(id => Array.IndexOf(deny, id) < 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var pimbByQh = _state.LastPImbTicksPerQuarter;

        foreach (var clientId in realTeams)
        {
            for (var qh = 0; qh < 4; qh++)
            {
                _state.NetPositions.TryGetValue((clientId, qh), out var positionTicks);
                var pimbTicks = pimbByQh[qh];
                var pnlTicks = checked(positionTicks * pimbTicks);

                var settlement = new ImbalanceSettlementEvent(
                    RoundNumber: _state.CurrentRoundNumber,
                    ClientId: clientId,
                    InstrumentId: BuildQuarterInstrumentDto(qh),
                    QuarterIndex: qh,
                    PositionTicks: positionTicks,
                    PImbTicks: pimbTicks,
                    ImbalancePnlTicks: pnlTicks,
                    TimestampNs: tsNs);

                // ResolvePrivateRouting switches on the event type and produces
                // private.imbalance.settlement.<clientId> for ImbalanceSettlementEvent.
                _publisher.PublishPrivate(clientId, settlement);
            }
        }

        _log.LogInformation(
            "Settled: emitted {Count} settlement rows across {Teams} teams",
            realTeams.Count * 4, realTeams.Count);
    }

    /// <summary>
    /// Build the canonical Phase 04 <see cref="InstrumentIdDto"/> for quarter index
    /// 0..3 using the synthetic 9999-01-01 hour anchor shared with
    /// <c>TradingCalendar.GenerateInstruments()</c>.
    /// </summary>
    private static InstrumentIdDto BuildQuarterInstrumentDto(int qh)
    {
        if (qh is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(qh), "quarter_index must be 0..3");
        }

        var start = SyntheticHourStart.AddMinutes(qh * 15);
        var end = start.AddMinutes(15);
        return new InstrumentIdDto(DeliveryAreaDe, start, end);
    }

    /// <summary>
    /// Build the routing-key form of the Phase 04 canonical quarter instrument —
    /// matches <c>InstrumentId.ToRoutingKey()</c> (<c>&lt;area&gt;.&lt;yyyyMMddHHmm&gt;-&lt;yyyyMMddHHmm&gt;</c>).
    /// </summary>
    private static string BuildQuarterInstrumentRoutingKey(int qh)
    {
        if (qh is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(qh), "quarter_index must be 0..3");
        }

        var start = SyntheticHourStart.AddMinutes(qh * 15);
        var end = start.AddMinutes(15);
        return $"{DeliveryAreaDe}.{start:yyyyMMddHHmm}-{end:yyyyMMddHHmm}";
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
