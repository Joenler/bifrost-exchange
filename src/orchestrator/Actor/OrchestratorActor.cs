using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Contracts.Internal.McLog;
using Bifrost.Contracts.Mc;
using Bifrost.Orchestrator.Grpc;
using Bifrost.Orchestrator.News;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.State;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BifrostState = Bifrost.Exchange.Application.RoundState.RoundState;
using ProtoRoundState = Bifrost.Contracts.Round.RoundState;
using ProtoState = Bifrost.Contracts.Round.State;

namespace Bifrost.Orchestrator.Actor;

/// <summary>
/// The single-reader drain loop for orchestrator state mutations. gRPC handlers,
/// the iteration-seed timer, and the heartbeat-tolerance monitor all produce
/// messages onto the shared <c>Channel&lt;IOrchestratorMessage&gt;</c>; this
/// actor is the ONLY place state is mutated.
/// </summary>
/// <remarks>
/// Load-on-startup + one reconciliation publish on boot. Fail-closed
/// persistence: a throw from <see cref="JsonStateStore.SaveAsync"/> rolls back
/// the in-memory state machine to its pre-apply snapshot before completing the
/// TCS with a typed rejection, so the wire NEVER advances past the disk.
///
/// Constructor takes all 11 collaborators (reader + machine + store + publisher
/// + topology + clock + options + ring + seed allocator + news library +
/// logger). The signature is LOCKED here — follow-up plans fill in the bodies
/// of the three stub collaborators (<see cref="RoundStateRingBuffer"/>,
/// <see cref="RoundSeedAllocator"/>, <see cref="FileSystemNewsLibrary"/>) WITHOUT
/// altering this class's constructor.
/// </remarks>
public sealed class OrchestratorActor : BackgroundService
{
    private readonly ChannelReader<IOrchestratorMessage> _reader;
    private readonly RoundStateMachine _machine;
    private readonly JsonStateStore _store;
    private readonly OrchestratorPublisher _publisher;
    private readonly OrchestratorRabbitMqTopology _topology;
    private readonly IClock _clock;
    private readonly IOptions<OrchestratorOptions> _opts;
    private readonly RoundStateRingBuffer _ring;
    private readonly RoundSeedAllocator _seedAllocator;
    private readonly INewsLibrary _newsLibrary;
    private readonly ILogger<OrchestratorActor> _logger;

    // Follow-up plan (event-emitting commands) may override a post-TryApply
    // success with a library-miss reject — captured here for the handler to
    // pick up.
    private string? _unknownLibraryKey;

    private OrchestratorState _state = default!;
    private long _publishSequence;

    public OrchestratorActor(
        ChannelReader<IOrchestratorMessage> reader,
        RoundStateMachine machine,
        JsonStateStore store,
        OrchestratorPublisher publisher,
        OrchestratorRabbitMqTopology topology,
        IClock clock,
        IOptions<OrchestratorOptions> opts,
        RoundStateRingBuffer ring,
        RoundSeedAllocator seedAllocator,
        INewsLibrary newsLibrary,
        ILogger<OrchestratorActor> logger)
    {
        _reader = reader;
        _machine = machine;
        _store = store;
        _publisher = publisher;
        _topology = topology;
        _clock = clock;
        _opts = opts;
        _ring = ring;
        _seedAllocator = seedAllocator;
        _newsLibrary = newsLibrary;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Declare topology (idempotent with any other producers).
        await _topology.DeclareAsync(stoppingToken);

        // Load-or-fresh-boot.
        OrchestratorState? loaded = _store.TryLoad();
        long nowNs = NowNs();
        if (loaded is null)
        {
            _state = OrchestratorState.FreshBoot(_opts.Value.MasterSeed, nowNs);
            await _store.SaveAsync(_state, stoppingToken);
            _logger.LogInformation(
                "Fresh boot - state=IterationOpen, master_seed={Seed}",
                _state.MasterSeed);
        }
        else
        {
            _state = loaded;
            _machine.RestoreFrom(_state);
            _logger.LogInformation(
                "Reloaded state - state={State}, round_number={RoundNumber}, paused={Paused}, blocked={Blocked}",
                _state.State,
                _state.RoundNumber,
                _state.Paused,
                _state.Blocked);
        }

        // One reconciliation publish on boot so downstream services reconcile.
        await PublishStateSnapshot(isReconciliation: true, stoppingToken);

        // Drain loop.
        try
        {
            await foreach (IOrchestratorMessage msg in _reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    switch (msg)
                    {
                        case McCommandMessage m:
                            await HandleMcCommandAsync(m, stoppingToken);
                            break;
                        case HeartbeatChangeMessage h:
                            await HandleHeartbeatChangeAsync(h, stoppingToken);
                            break;
                        case IterationSeedTickMessage:
                            await HandleIterationSeedTickAsync(stoppingToken);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Actor drain loop caught unhandled exception - continuing");

                    // Per-command handlers MUST complete their own TCS; if a
                    // McCommandMessage slipped through without completing,
                    // surface that as a typed internal-error reject.
                    if (msg is McCommandMessage uncompleted && !uncompleted.Tcs.Task.IsCompleted)
                    {
                        uncompleted.Tcs.TrySetResult(new McCommandResult
                        {
                            Success = false,
                            Message = $"internal error: {ex.GetType().Name}",
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("OrchestratorActor draining on cancel");
        }
    }

    private async Task HandleMcCommandAsync(McCommandMessage msg, CancellationToken ct)
    {
        McCommand cmd = msg.Cmd;
        McCommand.CommandOneofCase variant = cmd.CommandCase;

        RoundStateMachine.Outcome outcome = _machine.TryApply(cmd);

        string? errorMessage = null;

        // Transition or flag-change path: save-then-publish, with rollback on
        // persistence failure (the wire never advances past the disk).
        if (outcome.Success && (outcome.StateChanged || outcome.FlagsChanged))
        {
            OrchestratorState next = _state with
            {
                State = _machine.Current,
                Paused = _machine.Paused,
                PausedReason = GetPausedReason(),
                Blocked = _machine.Blocked,
                BlockedReason = GetBlockedReason(),
                LastTransitionNs = NowNs(),
                AbortReason = _machine.Current == BifrostState.Aborted ? AbortReasonFromCmd(cmd) : null,
                ScoredRoundsCompleted = _machine.Current == BifrostState.Settled && _state.State == BifrostState.Gate
                    ? _state.ScoredRoundsCompleted + 1
                    : _state.ScoredRoundsCompleted,
            };

            // On transition to AuctionOpen: allocate a fresh scored-round seed.
            // The stub allocator returns 0L; a follow-up plan swaps in the real
            // math. Tests in this plan tolerate ScenarioSeedInternal == 0.
            if (outcome.StateChanged && _machine.Current == BifrostState.AuctionOpen)
            {
                long newSeed = _seedAllocator.NextScoredRoundSeed(next.RoundNumber);
                next = next with { ScenarioSeedInternal = newSeed };
            }

            try
            {
                await _store.SaveAsync(next, ct);
                _state = next;
            }
            catch (Exception ex)
            {
                // Rollback - revert the state machine to the pre-apply snapshot
                // so Current, Paused, and Blocked all match what's on disk.
                _machine.RestoreFrom(_state);
                errorMessage = $"persistence failed: {FirstLine(ex.Message)}";
                _logger.LogError(ex, "Persistence failed during transition {Cmd}", variant);
            }

            if (errorMessage is null)
            {
                await PublishStateSnapshot(isReconciliation: false, ct);

                // Abort auto-emits a MarketAlert envelope so the imbalance
                // simulator can cancel in-flight settlement work.
                if (outcome.StateChanged && _machine.Current == BifrostState.Aborted)
                {
                    MarketAlertPayload alert = new(
                        Text: $"Round {_state.RoundNumber} aborted: {_state.AbortReason ?? "no reason"}",
                        Severity: "urgent");
                    await _publisher.PublishMarketAlertAsync(alert, ct);
                }
            }
        }

        // Event-emitting branch (NewsFire / NewsPublish / AlertUrgent /
        // ConfigSet / RegimeForce): succeeded with no state change. A follow-up
        // plan fills in the real branches; this plan ships the dispatch stub so
        // the constructor + call-site stay stable across plans.
        if (outcome.Success && !outcome.StateChanged && !outcome.FlagsChanged)
        {
            await HandleEventEmittingCommandAsync(cmd, ct);
            if (_unknownLibraryKey is not null)
            {
                errorMessage = $"unknown library key: {_unknownLibraryKey}";
                _unknownLibraryKey = null;
            }
        }

        // Audit-log every command (accepted or rejected).
        await AuditLogAsync(cmd, outcome, errorMessage, ct);

        // Complete the TCS with the typed result.
        McCommandResult result = BuildResult(outcome, errorMessage);
        msg.Tcs.TrySetResult(result);

        // SourceTag is captured on the message for future audit extension; this
        // plan's audit payload only carries operator_host.
        _ = msg.SourceTag;
    }

    private async Task HandleHeartbeatChangeAsync(HeartbeatChangeMessage h, CancellationToken ct)
    {
        if (!h.Healthy)
        {
            bool changed = _machine.ApplyBlock("heartbeat_lost");
            if (!changed)
            {
                return;
            }

            OrchestratorState next = _state with
            {
                Paused = true,
                PausedReason = "heartbeat_lost",
                Blocked = true,
                BlockedReason = "heartbeat_lost",
                LastTransitionNs = NowNs(),
            };

            try
            {
                await _store.SaveAsync(next, ct);
                _state = next;
                await PublishStateSnapshot(isReconciliation: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Persistence failed during heartbeat-loss handling");
            }
        }
        else
        {
            // Heartbeat-restored only logs. MC Resume is the sole clear path,
            // per the no-auto-clear requirement.
            _logger.LogInformation(
                "Heartbeat restored; MC Resume required to clear Blocked=true");
        }
    }

    private async Task HandleIterationSeedTickAsync(CancellationToken ct)
    {
        // D-22: the IterationSeedTimer runs under Paused=true (iteration seeds
        // are public clock-rolls). The rotation itself only fires while we are
        // in IterationOpen; ticks outside that window are silent no-ops so the
        // count + seed never advance during a scored round.
        if (_state.State != BifrostState.IterationOpen)
        {
            _logger.LogDebug(
                "IterationSeedTick received outside IterationOpen (state={State}) - skipping",
                _state.State);
            return;
        }

        int nextCount = _state.IterationSeedRotationCount + 1;
        long newIterationSeed = _seedAllocator.CurrentIterationSeed(nextCount);

        OrchestratorState next = _state with
        {
            IterationSeedRotationCount = nextCount,
            ScenarioSeedInternal = newIterationSeed,
            LastTransitionNs = NowNs(),
        };

        try
        {
            await _store.SaveAsync(next, ct);
            _state = next;
            await PublishStateSnapshot(isReconciliation: false, ct);
        }
        catch (Exception ex)
        {
            // Persistence failure on a seed rotation is non-fatal: the in-
            // memory state is unchanged (we never assigned _state) and the
            // next tick will retry. Log and continue.
            _logger.LogError(
                ex,
                "Persistence failed during iteration-seed tick (count={Count}) - skipping",
                nextCount);
        }
    }

    // Event-emitting command dispatch: NewsFire / NewsPublish / AlertUrgent /
    // ConfigSet / RegimeForce. Each branch publishes the corresponding wire
    // envelope WITHOUT mutating orchestrator state - the round state machine
    // already returned StateChanged=false / FlagsChanged=false for these
    // variants. Library-miss on NewsFire and unmapped-regime on RegimeForce
    // both surface a typed reject by setting _unknownLibraryKey, which the
    // caller in HandleMcCommandAsync converts into the McCommandResult message.
    //
    // RegimeForce routes to bifrost.mc / mc.regime.force (the quoter-inbound
    // exchange) rather than emitting events.regime_change directly: per Phase
    // 03 D-17, the quoter is the sole Event.RegimeChange emitter. The quoter
    // consumes this envelope, installs the regime, cancels-all + re-quotes,
    // and emits the public events.regime_change envelope itself.
    private async Task HandleEventEmittingCommandAsync(McCommand cmd, CancellationToken ct)
    {
        switch (cmd.CommandCase)
        {
            case McCommand.CommandOneofCase.NewsPublish:
                await _publisher.PublishNewsAsync(
                    new NewsPayload(
                        Text: cmd.NewsPublish?.Text ?? string.Empty,
                        LibraryKey: string.Empty,
                        Severity: "info"),
                    ct);
                break;

            case McCommand.CommandOneofCase.NewsFire:
            {
                string key = cmd.NewsFire?.LibraryKey ?? string.Empty;
                NewsLibraryEntry? entry = _newsLibrary.TryGet(key);
                if (entry is null)
                {
                    // Library-miss: the round state machine accepted the
                    // command (event-emitting commands are legal in any state)
                    // but the canned library has no matching entry. Surface
                    // the miss as a typed reject by setting _unknownLibraryKey;
                    // HandleMcCommandAsync converts it into the
                    // McCommandResult.Message before BuildResult runs. No
                    // envelope is published - the actor must publish zero
                    // events on a library miss per the SPEC acceptance test.
                    _unknownLibraryKey = key;
                    return;
                }

                await _publisher.PublishNewsAsync(
                    new NewsPayload(
                        Text: entry.Text,
                        LibraryKey: key,
                        Severity: entry.Severity),
                    ct);

                if (entry.Shock is not null)
                {
                    // News-library shocks have no quarter context; the wire
                    // DTO carries QuarterIndex=null and downstream consumers
                    // interpret per their subscription. The imbalance
                    // simulator's defense-in-depth check against operator-
                    // injected PhysicalShockCmds (which always carry a valid
                    // quarter index) is preserved by the separate
                    // PhysicalShockEvent DTO on the simulator's bind path.
                    await _publisher.PublishPhysicalShockAsync(
                        new PhysicalShockPayload(
                            Mw: entry.Shock.Mw,
                            Label: entry.Shock.Label,
                            Persistence: entry.Shock.Persistence),
                        ct);
                }

                break;
            }

            case McCommand.CommandOneofCase.AlertUrgent:
                await _publisher.PublishMarketAlertAsync(
                    new MarketAlertPayload(
                        Text: cmd.AlertUrgent?.Text ?? string.Empty,
                        Severity: "urgent"),
                    ct);
                break;

            case McCommand.CommandOneofCase.ConfigSet:
                // OldValue is empty: live-tune that reads its own previous
                // value lives in Phase 11; Phase 06's audit publish carries
                // the new value only. Consumers that need the old value can
                // recover it from their last reconciliation snapshot.
                await _publisher.PublishConfigChangeAsync(
                    new ConfigChangePayload(
                        Path: cmd.ConfigSet?.Path ?? string.Empty,
                        OldValue: string.Empty,
                        NewValue: cmd.ConfigSet?.Value ?? string.Empty),
                    ct);
                break;

            case McCommand.CommandOneofCase.RegimeForce:
            {
                Bifrost.Contracts.Events.Regime protoRegime =
                    cmd.RegimeForce?.Regime ?? Bifrost.Contracts.Events.Regime.Unspecified;
                string? regimeName = MapProtoRegimeToQuoterName(protoRegime);
                if (regimeName is null)
                {
                    // Unmapped regime (Unspecified or unknown enum value):
                    // reuse the same reject channel as NewsFire library-miss.
                    // The audit log + McCommandResult will both surface
                    // "unknown library key: regime:Unspecified" - intentional
                    // overload of the channel given the absence of a more
                    // specific reject code in the command result wire DTO.
                    _unknownLibraryKey = $"regime:{protoRegime}";
                    return;
                }

                // Wire shape matches McRegimeForceDto on the quoter side
                // (declared as object here to avoid a Bifrost.Orchestrator ->
                // Bifrost.Quoter ProjectReference inversion). Nonce is fresh
                // per publish so the quoter consumer has an idempotent
                // dedup-key alongside the envelope's CorrelationId.
                object payload = new
                {
                    regime = regimeName,
                    nonce = Guid.NewGuid(),
                };
                await _publisher.PublishRegimeForceAsync(payload, ct);
                break;
            }
        }
    }

    private static string? MapProtoRegimeToQuoterName(Bifrost.Contracts.Events.Regime protoRegime) =>
        protoRegime switch
        {
            Bifrost.Contracts.Events.Regime.Calm => "Calm",
            Bifrost.Contracts.Events.Regime.Trending => "Trending",
            Bifrost.Contracts.Events.Regime.Volatile => "Volatile",
            Bifrost.Contracts.Events.Regime.Shock => "Shock",
            _ => null,
        };

    private async Task PublishStateSnapshot(bool isReconciliation, CancellationToken ct)
    {
        long seq = Interlocked.Increment(ref _publishSequence);
        RoundStateChangedPayload payload = new(
            State: _state.State.ToString(),
            RoundNumber: _state.RoundNumber,
            ScenarioSeedOnWire: ComputeScenarioSeedOnWire(),
            TransitionNs: _state.LastTransitionNs,
            ExpectedNextTransitionNs: null,
            Paused: _state.Paused,
            PausedReason: _state.PausedReason,
            Blocked: _state.Blocked,
            BlockedReason: _state.BlockedReason,
            IsReconciliation: isReconciliation,
            IterationSeedRotationCount: _state.IterationSeedRotationCount,
            AbortReason: _state.AbortReason);

        await _publisher.PublishRoundStateChangedAsync(payload, seq, ct);
        _ring.AppendSnapshot(payload);
    }

    private long ComputeScenarioSeedOnWire()
    {
        // 0 during scored rounds; exposed during IterationOpen. The iteration
        // seed field is driven by a follow-up plan; this plan projects from
        // the persisted ScenarioSeedInternal (stub returns 0 today).
        return _state.State == BifrostState.IterationOpen ? _state.ScenarioSeedInternal : 0L;
    }

    private async Task AuditLogAsync(
        McCommand cmd,
        RoundStateMachine.Outcome outcome,
        string? persistenceError,
        CancellationToken ct)
    {
        string newStateJson = outcome.Success && outcome.StateChanged
            ? BuildNewStateJson(_state)
            : string.Empty;

        McCommandLogPayload payload = new(
            TimestampNs: NowNs(),
            Command: cmd.CommandCase.ToString(),
            ArgsJson: cmd.ToString(),
            Success: outcome.Success && persistenceError is null,
            Message: persistenceError ?? outcome.Message,
            NewStateJson: newStateJson,
            OperatorHostname: cmd.OperatorHost ?? string.Empty);

        string cmdSnake = OrchestratorRabbitMqTopology.ToSnakeCase(cmd.CommandCase.ToString());
        await _publisher.PublishMcCommandLogAsync(payload, cmdSnake, ct);
    }

    private string? GetPausedReason() => _machine.Paused
        ? (_state.PausedReason ?? "mc")
        : null;

    private string? GetBlockedReason() => _machine.Blocked
        ? (_state.BlockedReason ?? "heartbeat_lost")
        : null;

    private static string? AbortReasonFromCmd(McCommand cmd) =>
        cmd.CommandCase == McCommand.CommandOneofCase.Abort ? cmd.Abort?.Reason : null;

    private static string BuildNewStateJson(OrchestratorState s) =>
        JsonSerializer.Serialize(new
        {
            state = s.State.ToString(),
            round_number = s.RoundNumber,
            last_transition_ns = s.LastTransitionNs,
        });

    private McCommandResult BuildResult(RoundStateMachine.Outcome outcome, string? persistenceError)
    {
        ProtoRoundState newState = new()
        {
            State = MapToProtoState(_state.State),
            RoundNumber = _state.RoundNumber,
            ScenarioSeed = ComputeScenarioSeedOnWire(),
            TransitionNs = _state.LastTransitionNs,
        };

        return new McCommandResult
        {
            Success = outcome.Success && persistenceError is null,
            Message = persistenceError ?? outcome.Message,
            NewState = newState,
        };
    }

    private static ProtoState MapToProtoState(BifrostState s) => s switch
    {
        BifrostState.IterationOpen => ProtoState.IterationOpen,
        BifrostState.AuctionOpen => ProtoState.AuctionOpen,
        BifrostState.AuctionClosed => ProtoState.AuctionClosed,
        BifrostState.RoundOpen => ProtoState.RoundOpen,
        BifrostState.Gate => ProtoState.Gate,
        BifrostState.Settled => ProtoState.Settled,
        BifrostState.Aborted => ProtoState.Aborted,
        _ => ProtoState.Unspecified,
    };

    private long NowNs() => _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;

    private static string FirstLine(string s) => s.Split('\n')[0];
}
