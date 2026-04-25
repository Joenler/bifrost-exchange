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

    private Task HandleIterationSeedTickAsync(CancellationToken ct)
    {
        // A follow-up plan fills in: increment IterationSeedRotationCount, call
        // _seedAllocator.CurrentIterationSeed(nextCount), persist, publish.
        // This plan ships the drain-arm stub so the constructor + dispatch
        // signature are stable across plans.
        _logger.LogDebug(
            "IterationSeedTickMessage received - follow-up plan implements rotation");
        _ = ct;
        return Task.CompletedTask;
    }

    // A follow-up plan fills in NewsFire / NewsPublish / AlertUrgent /
    // ConfigSet / RegimeForce branches inside this method. Bodies land
    // WITHOUT constructor changes to OrchestratorActor.
    private Task HandleEventEmittingCommandAsync(McCommand cmd, CancellationToken ct)
    {
        _ = cmd;
        _ = ct;
        _ = _newsLibrary;
        return Task.CompletedTask;
    }

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
