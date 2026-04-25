using Bifrost.Contracts.Mc;
using BifrostState = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.Orchestrator.State;

/// <summary>
/// Pure-logic state machine covering the 7-value <see cref="BifrostState"/>
/// × 21-variant <see cref="McCommand.CommandOneofCase"/> matrix (147 cells).
/// <see cref="TryApply"/> returns a typed transition outcome; the caller
/// (the orchestrator actor) is responsible for persisting + publishing on
/// a successful transition.
/// </summary>
/// <remarks>
/// Thread-safety: NOT thread-safe. Must be driven from the single-reader
/// orchestrator drain loop that owns the <c>Channel&lt;McCommand&gt;</c>.
/// Event-emitting commands (ForecastRevise, RegimeForce, News*, AlertUrgent,
/// PhysicalShock, Team*, ConfigSet, LeaderboardReveal) are legal in any
/// state and never change <see cref="Current"/>. Flow-control commands
/// (Pause / Resume) are orthogonal to <see cref="Current"/>; they toggle
/// the Paused / Blocked flags without changing the state.
/// </remarks>
public sealed class RoundStateMachine
{
    public BifrostState Current { get; private set; } = BifrostState.IterationOpen;

    public bool Paused { get; private set; }

    public bool Blocked { get; private set; }

    public string? PausedReason { get; private set; }

    public string? BlockedReason { get; private set; }

    /// <summary>
    /// Restore the in-memory machine from a persisted snapshot. Called once
    /// by the orchestrator actor at startup when
    /// <see cref="JsonStateStore.TryLoad"/> returns non-null.
    /// </summary>
    public void RestoreFrom(OrchestratorState snapshot)
    {
        Current = snapshot.State;
        Paused = snapshot.Paused;
        PausedReason = snapshot.PausedReason;
        Blocked = snapshot.Blocked;
        BlockedReason = snapshot.BlockedReason;
    }

    /// <summary>
    /// Outcome of a <see cref="TryApply"/> call. The orchestrator actor
    /// translates this into <c>McCommandResult</c> on the gRPC wire, into
    /// <c>RoundStateChangedPayload</c> on <c>bifrost.round.v1</c>, and
    /// into the <see cref="OrchestratorState"/> snapshot written to disk.
    /// </summary>
    /// <param name="Success">True if the command was accepted.</param>
    /// <param name="NewState">State after apply; equals <see cref="Current"/> on rejection.</param>
    /// <param name="Message">Human-readable outcome string; never null or empty.</param>
    /// <param name="StateChanged">True when <see cref="Current"/> was mutated (caller publishes a transition).</param>
    /// <param name="FlagsChanged">True when Paused or Blocked mutated (caller publishes a flag change).</param>
    public sealed record Outcome(
        bool Success,
        BifrostState NewState,
        string Message,
        bool StateChanged,
        bool FlagsChanged);

    /// <summary>
    /// Apply the command to the machine. Pure-logic: no side effects beyond
    /// the machine's own fields. Never throws on the 147-cell product of
    /// legal state × legal command variants; unset oneof returns a typed
    /// rejection via <see cref="Reject"/>.
    /// </summary>
    public Outcome TryApply(McCommand cmd)
    {
        McCommand.CommandOneofCase variant = cmd.CommandCase;

        // Unset oneof: callers should have short-circuited, but the matrix is
        // exhaustive by spec, so we return a typed reject instead of throwing.
        if (variant == McCommand.CommandOneofCase.None)
        {
            return Reject("no command set");
        }

        // Flow-control: Pause / Resume are orthogonal to Current; legal anywhere.
        if (variant == McCommand.CommandOneofCase.Pause)
        {
            if (Paused)
            {
                return Ok("already paused");
            }

            Paused = true;
            PausedReason = "mc";
            return new Outcome(
                Success: true,
                NewState: Current,
                Message: "paused",
                StateChanged: false,
                FlagsChanged: true);
        }

        if (variant == McCommand.CommandOneofCase.Resume)
        {
            if (!Paused && !Blocked)
            {
                return Ok("already running");
            }

            Paused = false;
            PausedReason = null;
            Blocked = false;
            BlockedReason = null;
            return new Outcome(
                Success: true,
                NewState: Current,
                Message: "resumed",
                StateChanged: false,
                FlagsChanged: true);
        }

        // Event-emitting commands: legal in ANY state, never transition Current.
        if (IsEventEmitting(variant))
        {
            return Ok($"event-emitting: {variant}");
        }

        // Blocked gate: transitions rejected until MC Resume (SPEC Req 11).
        if (Blocked)
        {
            return Reject($"transitions blocked: {BlockedReason ?? "unknown"}");
        }

        // State-specific transitions (SPEC Req 1's 147-cell table).
        BifrostState? next = (Current, variant) switch
        {
            // IterationOpen transitions
            (BifrostState.IterationOpen, McCommand.CommandOneofCase.AuctionOpen) => BifrostState.AuctionOpen,
            (BifrostState.IterationOpen, McCommand.CommandOneofCase.EventEnd)    => BifrostState.IterationOpen,

            // AuctionOpen transitions
            (BifrostState.AuctionOpen, McCommand.CommandOneofCase.AuctionClose) => BifrostState.AuctionClosed,
            (BifrostState.AuctionOpen, McCommand.CommandOneofCase.Abort)        => BifrostState.Aborted,

            // AuctionClosed transitions
            (BifrostState.AuctionClosed, McCommand.CommandOneofCase.RoundStart) => BifrostState.RoundOpen,
            (BifrostState.AuctionClosed, McCommand.CommandOneofCase.Abort)      => BifrostState.Aborted,

            // RoundOpen transitions (RoundEnd is an alias for Gate)
            (BifrostState.RoundOpen, McCommand.CommandOneofCase.Gate)     => BifrostState.Gate,
            (BifrostState.RoundOpen, McCommand.CommandOneofCase.RoundEnd) => BifrostState.Gate,
            (BifrostState.RoundOpen, McCommand.CommandOneofCase.Abort)    => BifrostState.Aborted,

            // Gate transitions
            (BifrostState.Gate, McCommand.CommandOneofCase.Settle) => BifrostState.Settled,
            (BifrostState.Gate, McCommand.CommandOneofCase.Abort)  => BifrostState.Aborted,

            // Settled transitions
            (BifrostState.Settled, McCommand.CommandOneofCase.NextRound) => BifrostState.IterationOpen,
            (BifrostState.Settled, McCommand.CommandOneofCase.EventEnd)  => BifrostState.Settled,

            // Aborted transitions
            (BifrostState.Aborted, McCommand.CommandOneofCase.NextRound) => BifrostState.IterationOpen,
            (BifrostState.Aborted, McCommand.CommandOneofCase.EventEnd)  => BifrostState.Aborted,

            _ => (BifrostState?)null,
        };

        if (next is null)
        {
            return Reject($"illegal transition: {variant} from {Current}");
        }

        // EventEnd is a meta-op: Current stays put; the caller flips EventOver.
        if (variant == McCommand.CommandOneofCase.EventEnd)
        {
            return Ok($"event ended from {Current}");
        }

        // Idempotent no-op per SPEC Req 1 bullet 8: duplicate transition to
        // the same state succeeds without re-publishing.
        if (next.Value == Current)
        {
            return new Outcome(
                Success: true,
                NewState: Current,
                Message: $"already in {Current}",
                StateChanged: false,
                FlagsChanged: false);
        }

        Current = next.Value;
        return new Outcome(
            Success: true,
            NewState: Current,
            Message: $"transitioned to {Current}",
            StateChanged: true,
            FlagsChanged: false);
    }

    /// <summary>
    /// External callers (the orchestrator's heartbeat-tolerance monitor) call
    /// this when the gateway heartbeat flips unhealthy. Sets both Blocked and
    /// Paused so downstream reconciliation publishes reflect the combined
    /// operator-visible flag state. Returns false on idempotent no-op
    /// (already blocked with the same reason).
    /// </summary>
    public bool ApplyBlock(string reason)
    {
        if (Blocked && BlockedReason == reason)
        {
            return false;
        }

        Blocked = true;
        BlockedReason = reason;
        Paused = true;
        PausedReason = reason;
        return true;
    }

    private Outcome Ok(string message) => new(
        Success: true,
        NewState: Current,
        Message: message,
        StateChanged: false,
        FlagsChanged: false);

    private Outcome Reject(string message) => new(
        Success: false,
        NewState: Current,
        Message: message,
        StateChanged: false,
        FlagsChanged: false);

    private static bool IsEventEmitting(McCommand.CommandOneofCase variant) => variant switch
    {
        McCommand.CommandOneofCase.ForecastRevise or
        McCommand.CommandOneofCase.RegimeForce or
        McCommand.CommandOneofCase.NewsFire or
        McCommand.CommandOneofCase.NewsPublish or
        McCommand.CommandOneofCase.AlertUrgent or
        McCommand.CommandOneofCase.PhysicalShock or
        McCommand.CommandOneofCase.TeamKick or
        McCommand.CommandOneofCase.TeamReset or
        McCommand.CommandOneofCase.ConfigSet or
        McCommand.CommandOneofCase.LeaderboardReveal => true,
        _ => false,
    };
}
