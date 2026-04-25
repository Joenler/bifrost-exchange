using Bifrost.Contracts.Mc;

namespace Bifrost.Orchestrator.Actor;

/// <summary>
/// Union type for every message the <see cref="OrchestratorActor"/> drain loop
/// accepts. Three concrete variants, one per producer:
/// <list type="bullet">
///   <item><see cref="McCommandMessage"/>: gRPC-origin (or in-process test)
///         commands with TCS rendezvous.</item>
///   <item><see cref="HeartbeatChangeMessage"/>: from the RabbitMQ-backed
///         gateway heartbeat source (landed in a follow-up plan).</item>
///   <item><see cref="IterationSeedTickMessage"/>: from the iteration-seed
///         rotation timer (landed in a follow-up plan).</item>
/// </list>
/// There is deliberately NO AutoAdvanceTickMessage variant — the no-auto-advance
/// requirement locks zero timer-driven state transitions.
/// </summary>
public abstract record IOrchestratorMessage(long TsNs);

/// <summary>
/// Carries an <see cref="McCommand"/> plus a <see cref="TaskCompletionSource{TResult}"/>
/// the drain loop completes with the typed <see cref="McCommandResult"/>. The
/// <paramref name="SourceTag"/> is a free-form string (e.g. "grpc",
/// "test-stress") written into the audit log alongside the operator hostname.
/// </summary>
public sealed record McCommandMessage(
    long TsNs,
    McCommand Cmd,
    TaskCompletionSource<McCommandResult> Tcs,
    string SourceTag)
    : IOrchestratorMessage(TsNs);

/// <summary>
/// Signals a change in the gateway heartbeat health flag. <paramref name="Healthy"/>=false
/// sets Blocked+Paused with <c>reason="heartbeat_lost"</c>; <paramref name="Healthy"/>=true
/// only logs — the spec requires an explicit MC Resume to clear the block.
/// </summary>
public sealed record HeartbeatChangeMessage(
    long TsNs,
    bool Healthy)
    : IOrchestratorMessage(TsNs);

/// <summary>
/// Fires every iteration-seed-rotation-seconds during IterationOpen. Drains the
/// rotation increment + seed-allocator call + publish through the same
/// single-writer queue so no lock is required on the seed-rotation path.
/// </summary>
public sealed record IterationSeedTickMessage(
    long TsNs)
    : IOrchestratorMessage(TsNs);
