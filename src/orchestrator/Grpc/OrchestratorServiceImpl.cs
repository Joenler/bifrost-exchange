using System.Text.RegularExpressions;
using System.Threading.Channels;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Contracts.Mc;
using Bifrost.Orchestrator.Actor;
using Bifrost.Time;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProtoRoundState = Bifrost.Contracts.Round.RoundState;
using ProtoState = Bifrost.Contracts.Round.State;

namespace Bifrost.Orchestrator.Grpc;

/// <summary>
/// gRPC server implementation for <c>bifrost.mc.v1.OrchestratorService</c>.
///
/// Two RPCs:
/// <list type="bullet">
///   <item><see cref="Execute"/> — unary; every business-logic failure
///         (illegal transition, missing oneof, oversized operator_host,
///         control-char operator_host, missing confirm on a destructive
///         command, internal exception) is mapped to
///         <see cref="McCommandResult"/> with <c>success=false</c> and a
///         human-readable category-prefixed message. The handler NEVER
///         returns a gRPC <see cref="RpcException"/> for business-logic
///         failures; only <see cref="OperationCanceledException"/>
///         (transport cancel) is allowed to propagate.</item>
///   <item><see cref="WatchRoundState"/> — server-streaming; on connect
///         replays from the ring buffer (capacity 128 per CONTEXT D-15);
///         tails live snapshots until the client disconnects. A resume
///         request whose <c>last_seen_transition_ns</c> falls outside the
///         ring receives a synthetic resume-reset (the current snapshot)
///         — never a <c>REREGISTER_REQUIRED</c> reject.</item>
/// </list>
/// </summary>
/// <remarks>
/// CONTEXT D-27 exception → <see cref="McCommandResult"/> category mapping
/// is implemented across this class and the actor's drain loop:
/// <list type="bullet">
///   <item><c>operator_host</c> validation → <c>"operator_host: ..."</c></item>
///   <item>Missing oneof → <c>"no command set"</c></item>
///   <item><c>confirm=false</c> on destructive → <c>"confirm required for {cmd}"</c></item>
///   <item><see cref="RoundStateMachine"/> illegal transition →
///         message returned by <see cref="RoundStateMachine.TryApply"/>
///         (typically <c>"illegal transition: {cmd} from {state}"</c>)</item>
///   <item><c>JsonStateStore.SaveAsync</c> throw → handled in actor;
///         message <c>"persistence failed: {first-line}"</c></item>
///   <item><c>NewsFireCmd</c> unknown library_key → handled in actor;
///         message <c>"unknown library key: {key}"</c></item>
///   <item>Any other unexpected throw inside <see cref="Execute"/> →
///         <c>"internal error: {ExceptionTypeName}"</c></item>
/// </list>
/// </remarks>
public sealed class OrchestratorServiceImpl : OrchestratorService.OrchestratorServiceBase
{
    private readonly ChannelWriter<IOrchestratorMessage> _writer;
    private readonly RoundStateRingBuffer _ring;
    private readonly IClock _clock;
    private readonly IOptions<OrchestratorOptions> _opts;
    private readonly ILogger<OrchestratorServiceImpl> _logger;

    // Pre-compiled regex for control-char detection in operator_host. Per
    // CONTEXT D-26 the threat surface is a malicious operator string carrying
    // any byte in 0x00-0x08 or 0x0A-0x1F (i.e. C0 control chars except TAB).
    private static readonly Regex ControlCharRegex = new(
        @"[\x00-\x08\x0A-\x1F]",
        RegexOptions.Compiled);

    // Scored-parameter regex for ConfigSet destructive classification (D-32).
    // Paths matching this regex are considered scored configuration; the
    // orchestrator REQUIRES confirm=true to route them. The orchestrator does
    // NOT validate the value or apply the change — it only emits an event;
    // downstream consumers (quoter, exchange, imbalance) are responsible for
    // validating and applying their own keys.
    private static readonly Regex ScoredParameterKeyRegex = new(
        @"^(guards|scoring|regime|imbalance)\..*",
        RegexOptions.Compiled);

    public OrchestratorServiceImpl(
        ChannelWriter<IOrchestratorMessage> writer,
        RoundStateRingBuffer ring,
        IClock clock,
        IOptions<OrchestratorOptions> opts,
        ILogger<OrchestratorServiceImpl> logger)
    {
        _writer = writer;
        _ring = ring;
        _clock = clock;
        _opts = opts;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<McCommandResult> Execute(
        McCommand request,
        ServerCallContext context)
    {
        try
        {
            // 1. Structural validation of operator_host (D-26).
            int hostLen = request.OperatorHost?.Length ?? 0;
            if (hostLen > 256)
            {
                return Reject("operator_host: invalid (>256 chars)");
            }
            if (!string.IsNullOrEmpty(request.OperatorHost)
                && ControlCharRegex.IsMatch(request.OperatorHost))
            {
                return Reject("operator_host: invalid (control characters)");
            }

            // 2. Missing oneof.
            if (request.CommandCase == McCommand.CommandOneofCase.None)
            {
                return Reject("no command set");
            }

            // 3. DryRun path — preview only, no state mutation.
            if (request.DryRun)
            {
                return BuildDryRunResult(request);
            }

            // 4. Destructive-command confirm-required (D-27).
            if (IsDestructive(request) && !request.Confirm)
            {
                return Reject($"confirm required for {request.CommandCase}");
            }

            // 5. Enqueue + TaskCompletionSource rendezvous. The actor drain
            //    loop completes the TCS with the typed McCommandResult.
            TaskCompletionSource<McCommandResult> tcs = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            long tsNs = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await _writer.WriteAsync(
                new McCommandMessage(tsNs, request, tcs, "grpc"),
                context.CancellationToken);
            return await tcs.Task.WaitAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Transport-level cancellation — the only exception allowed to
            // escape Execute per CONTEXT D-27. gRPC translates this to a
            // CANCELLED status code on the wire.
            throw;
        }
        catch (Exception ex)
        {
            // Per D-27: every other exception type maps to a typed reject.
            // Logging captures the exception detail for forensic replay; the
            // wire only carries the type name (no PII / stack frame leak).
            _logger.LogError(
                ex,
                "OrchestratorServiceImpl.Execute caught unexpected exception for {CommandCase}",
                request?.CommandCase);
            return Reject($"internal error: {ex.GetType().Name}");
        }
    }

    /// <inheritdoc />
    public override async Task WatchRoundState(
        WatchRoundStateRequest request,
        IServerStreamWriter<ProtoRoundState> responseStream,
        ServerCallContext context)
    {
        // Subscribe BEFORE replaying the ring tail so a snapshot appended
        // between SnapshotInOrder() and Subscribe() is not silently dropped.
        // The subscriber will receive any duplicate live snapshots after the
        // replay; a duplicate is far preferable to a missed transition (the
        // wire-level state model is idempotent — clients dedupe on
        // transition_ns).
        using IDisposable unsubscribe = _ring.Subscribe(
            out ChannelReader<RoundStateChangedPayload> liveReader);

        long lastSeen = request.LastSeenTransitionNs;
        IReadOnlyList<RoundStateChangedPayload> ringSnapshot = _ring.SnapshotInOrder();

        // Bail-out helper: every WriteAsync site checks cancellation BEFORE
        // sending so we don't push into a torn-down stream. The
        // CancellationToken-aware WriteAsync overload on IServerStreamWriter
        // is an optional extension that some implementations (notably the
        // in-process test harness) decline with NotSupportedException, so we
        // call the no-token override and gate on context.CancellationToken
        // explicitly.
        if (lastSeen == 0)
        {
            // Fresh connect — stream the current snapshot first if the ring
            // is non-empty, otherwise wait silently for the first live
            // snapshot to land via the subscriber channel.
            RoundStateChangedPayload? current = _ring.Current();
            if (current is not null)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await responseStream.WriteAsync(MapToProto(current));
            }
        }
        else
        {
            // Resume from lastSeen.
            //   Case A — resume point IS in the ring: replay every snapshot
            //            with TransitionNs > lastSeen.
            //   Case B — resume point is OLDER than the ring's tail (i.e.
            //            every cached snapshot is newer than lastSeen, and
            //            the request is for a transition we no longer have):
            //            stream the current snapshot as a synthetic
            //            resume-reset.
            //   Case C — caller is up-to-date (every cached snapshot is
            //            <= lastSeen): also stream current as a no-op
            //            keepalive so the client knows the connection is
            //            live and the ring is non-empty.
            int afterIdx = -1;
            for (int i = 0; i < ringSnapshot.Count; i++)
            {
                if (ringSnapshot[i].TransitionNs > lastSeen)
                {
                    afterIdx = i;
                    break;
                }
            }

            bool resumeOlderThanRing =
                ringSnapshot.Count > 0 && ringSnapshot[0].TransitionNs > lastSeen;

            if (resumeOlderThanRing)
            {
                // Case B — caller's resume point fell out of the ring.
                // Synthetic resume-reset: stream the current snapshot.
                RoundStateChangedPayload? current = _ring.Current();
                if (current is not null)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    await responseStream.WriteAsync(MapToProto(current));
                }
            }
            else if (afterIdx >= 0)
            {
                // Case A — replay the ring tail.
                for (int i = afterIdx; i < ringSnapshot.Count; i++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    await responseStream.WriteAsync(MapToProto(ringSnapshot[i]));
                }
            }
            else
            {
                // Case C — caller is up-to-date or ring is empty. Echo the
                // current snapshot if any so the client has a canonical
                // up-to-date marker.
                RoundStateChangedPayload? current = _ring.Current();
                if (current is not null)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    await responseStream.WriteAsync(MapToProto(current));
                }
            }
        }

        // Tail live snapshots until the client disconnects (the cancellation
        // token fires when the gRPC stream is torn down). Treat
        // OperationCanceledException as a normal-disconnect signal — the
        // server SHOULD NOT propagate it to the gRPC layer as an error.
        try
        {
            await foreach (RoundStateChangedPayload snapshot in
                liveReader.ReadAllAsync(context.CancellationToken))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await responseStream.WriteAsync(MapToProto(snapshot));
            }
        }
        catch (OperationCanceledException)
            when (context.CancellationToken.IsCancellationRequested)
        {
            // Client disconnected — graceful tear-down. Emit nothing.
        }
    }

    private static ProtoRoundState MapToProto(RoundStateChangedPayload p) =>
        new()
        {
            State = MapStateName(p.State),
            RoundNumber = p.RoundNumber,
            ScenarioSeed = p.ScenarioSeedOnWire,
            TransitionNs = p.TransitionNs,
            ExpectedNextTransitionNs = p.ExpectedNextTransitionNs.GetValueOrDefault(),
        };

    private static ProtoState MapStateName(string name) => name switch
    {
        "IterationOpen" => ProtoState.IterationOpen,
        "AuctionOpen" => ProtoState.AuctionOpen,
        "AuctionClosed" => ProtoState.AuctionClosed,
        "RoundOpen" => ProtoState.RoundOpen,
        "Gate" => ProtoState.Gate,
        "Settled" => ProtoState.Settled,
        "Aborted" => ProtoState.Aborted,
        _ => ProtoState.Unspecified,
    };

    private static bool IsDestructive(McCommand cmd) => cmd.CommandCase switch
    {
        McCommand.CommandOneofCase.Gate => true,
        McCommand.CommandOneofCase.Settle => true,
        McCommand.CommandOneofCase.Abort => true,
        McCommand.CommandOneofCase.NewsFire => true,
        McCommand.CommandOneofCase.TeamKick => true,
        McCommand.CommandOneofCase.RegimeForce => true,
        McCommand.CommandOneofCase.ConfigSet
            when cmd.ConfigSet is not null
                 && ScoredParameterKeyRegex.IsMatch(cmd.ConfigSet.Path ?? string.Empty) => true,
        _ => false,
    };

    private static McCommandResult Reject(string message) => new()
    {
        Success = false,
        Message = message,
    };

    private static McCommandResult BuildDryRunResult(McCommand request)
    {
        // DryRun emits a synthetic preview without touching the actor. The
        // payload is a stable string the MC console renders verbatim; future
        // plans may extend this to include a projected new-state snapshot.
        string preview = $"DRY-RUN: {request.CommandCase}";
        return new McCommandResult
        {
            Success = true,
            Message = $"dry run — {request.CommandCase}",
            DryRunPayload = preview,
        };
    }
}
