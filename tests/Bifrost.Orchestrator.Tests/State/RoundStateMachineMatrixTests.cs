using Bifrost.Contracts.Mc;
using Bifrost.Orchestrator.State;
using Xunit;
using BifrostState = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.Orchestrator.Tests.State;

/// <summary>
/// Matrix gate for <see cref="RoundStateMachine"/>: every cell in the
/// 7-state x 21-command matrix (147 cells) returns a valid Outcome with
/// a non-null, non-empty message. The truth source is the §Requirements
/// Rule 1 table in the Phase 06 SPEC.
/// </summary>
public sealed class RoundStateMachineMatrixTests
{
    // All 21 command variants = CommandOneofCase values 4..24 (None=0 is
    // tested separately via the unset-oneof path in RoundStateMachine.TryApply).
    private static readonly McCommand.CommandOneofCase[] AllCommands =
    {
        McCommand.CommandOneofCase.AuctionOpen,
        McCommand.CommandOneofCase.AuctionClose,
        McCommand.CommandOneofCase.RoundStart,
        McCommand.CommandOneofCase.RoundEnd,
        McCommand.CommandOneofCase.Gate,
        McCommand.CommandOneofCase.Settle,
        McCommand.CommandOneofCase.NextRound,
        McCommand.CommandOneofCase.Pause,
        McCommand.CommandOneofCase.Resume,
        McCommand.CommandOneofCase.Abort,
        McCommand.CommandOneofCase.ForecastRevise,
        McCommand.CommandOneofCase.RegimeForce,
        McCommand.CommandOneofCase.NewsFire,
        McCommand.CommandOneofCase.NewsPublish,
        McCommand.CommandOneofCase.AlertUrgent,
        McCommand.CommandOneofCase.PhysicalShock,
        McCommand.CommandOneofCase.TeamKick,
        McCommand.CommandOneofCase.TeamReset,
        McCommand.CommandOneofCase.ConfigSet,
        McCommand.CommandOneofCase.LeaderboardReveal,
        McCommand.CommandOneofCase.EventEnd,
    };

    private static readonly BifrostState[] AllStates =
    {
        BifrostState.IterationOpen,
        BifrostState.AuctionOpen,
        BifrostState.AuctionClosed,
        BifrostState.RoundOpen,
        BifrostState.Gate,
        BifrostState.Settled,
        BifrostState.Aborted,
    };

    /// <summary>
    /// Enumerates the 7 x 21 = 147 Cartesian-product cells. xUnit theory
    /// data source.
    /// </summary>
    public static IEnumerable<object[]> MatrixCells()
    {
        foreach (BifrostState state in AllStates)
        {
            foreach (McCommand.CommandOneofCase cmd in AllCommands)
            {
                yield return new object[] { state, cmd };
            }
        }
    }

    /// <summary>
    /// Cell-coverage proof: every (state, command) pair in the 147-cell
    /// matrix returns a non-null <see cref="RoundStateMachine.Outcome"/>
    /// with a non-empty message string. No cell throws, no cell returns a
    /// default-valued Outcome with a blank message.
    /// </summary>
    [Theory]
    [MemberData(nameof(MatrixCells))]
    public void TryApply_NeverThrows(BifrostState initial, McCommand.CommandOneofCase variant)
    {
        RoundStateMachine machine = new();
        machine.RestoreFrom(OrchestratorState.FreshBoot(0, 0) with { State = initial });

        McCommand cmd = BuildCommand(variant);
        RoundStateMachine.Outcome outcome = machine.TryApply(cmd);

        Assert.NotNull(outcome);
        Assert.False(
            string.IsNullOrWhiteSpace(outcome.Message),
            $"({initial}, {variant}) produced an empty outcome message");
    }

    /// <summary>
    /// Spot-check the SPEC-authoritative legal transitions + a handful of
    /// illegal cells so the matrix test has a readable reference for what
    /// "success" means per cell.
    /// </summary>
    [Theory]
    [InlineData(BifrostState.IterationOpen, McCommand.CommandOneofCase.AuctionOpen,  true,  BifrostState.AuctionOpen)]
    [InlineData(BifrostState.AuctionOpen,   McCommand.CommandOneofCase.AuctionClose, true,  BifrostState.AuctionClosed)]
    [InlineData(BifrostState.AuctionClosed, McCommand.CommandOneofCase.RoundStart,   true,  BifrostState.RoundOpen)]
    [InlineData(BifrostState.RoundOpen,     McCommand.CommandOneofCase.Gate,         true,  BifrostState.Gate)]
    [InlineData(BifrostState.RoundOpen,     McCommand.CommandOneofCase.RoundEnd,     true,  BifrostState.Gate)]
    [InlineData(BifrostState.Gate,          McCommand.CommandOneofCase.Settle,       true,  BifrostState.Settled)]
    [InlineData(BifrostState.Settled,       McCommand.CommandOneofCase.NextRound,    true,  BifrostState.IterationOpen)]
    // Abort legal from all four scored-round states (SPEC Req 12).
    [InlineData(BifrostState.AuctionOpen,   McCommand.CommandOneofCase.Abort,        true,  BifrostState.Aborted)]
    [InlineData(BifrostState.AuctionClosed, McCommand.CommandOneofCase.Abort,        true,  BifrostState.Aborted)]
    [InlineData(BifrostState.RoundOpen,     McCommand.CommandOneofCase.Abort,        true,  BifrostState.Aborted)]
    [InlineData(BifrostState.Gate,          McCommand.CommandOneofCase.Abort,        true,  BifrostState.Aborted)]
    // Aborted -> IterationOpen on NextRound (SPEC Req 12).
    [InlineData(BifrostState.Aborted,       McCommand.CommandOneofCase.NextRound,    true,  BifrostState.IterationOpen)]
    // Abort is NOT legal from IterationOpen, Settled, or Aborted.
    [InlineData(BifrostState.IterationOpen, McCommand.CommandOneofCase.Abort,        false, BifrostState.IterationOpen)]
    [InlineData(BifrostState.Settled,       McCommand.CommandOneofCase.Abort,        false, BifrostState.Settled)]
    [InlineData(BifrostState.Aborted,       McCommand.CommandOneofCase.Abort,        false, BifrostState.Aborted)]
    // Sample illegal transitions.
    [InlineData(BifrostState.IterationOpen, McCommand.CommandOneofCase.Gate,         false, BifrostState.IterationOpen)]
    [InlineData(BifrostState.AuctionOpen,   McCommand.CommandOneofCase.Settle,       false, BifrostState.AuctionOpen)]
    [InlineData(BifrostState.Gate,          McCommand.CommandOneofCase.AuctionOpen,  false, BifrostState.Gate)]
    public void TryApply_TransitionCellHasExpectedOutcome(
        BifrostState initial,
        McCommand.CommandOneofCase variant,
        bool expectSuccess,
        BifrostState expectedNewState)
    {
        RoundStateMachine machine = new();
        machine.RestoreFrom(OrchestratorState.FreshBoot(0, 0) with { State = initial });

        RoundStateMachine.Outcome outcome = machine.TryApply(BuildCommand(variant));

        Assert.Equal(expectSuccess, outcome.Success);
        Assert.Equal(expectedNewState, outcome.NewState);
        if (expectSuccess)
        {
            Assert.Equal(expectedNewState, machine.Current);
        }
        else
        {
            // Rejected commands leave Current untouched.
            Assert.Equal(initial, machine.Current);
        }
    }

    /// <summary>
    /// Event-emitting commands are legal in any state and never change
    /// <see cref="RoundStateMachine.Current"/>. Picks NewsPublish as the
    /// representative; all 10 event-emitting commands share the
    /// IsEventEmitting branch.
    /// </summary>
    [Fact]
    public void TryApply_EventEmittingCommand_LeavesCurrentUnchanged()
    {
        RoundStateMachine machine = new();
        machine.RestoreFrom(OrchestratorState.FreshBoot(0, 0) with { State = BifrostState.Gate });

        BifrostState before = machine.Current;
        RoundStateMachine.Outcome outcome = machine.TryApply(
            BuildCommand(McCommand.CommandOneofCase.NewsPublish));

        Assert.True(outcome.Success);
        Assert.Equal(before, machine.Current);
        Assert.False(outcome.StateChanged);
        Assert.False(outcome.FlagsChanged);
    }

    /// <summary>
    /// Idempotent no-op convention (SPEC Req 1 bullet 8): a transition
    /// command whose target equals Current reports success without
    /// mutating. Demonstrated here via the Aborted-to-Aborted EventEnd
    /// cell, which passes the pattern-match but reports the meta-op message.
    /// </summary>
    [Fact]
    public void TryApply_EventEndFromAborted_LeavesCurrentAndReportsSuccess()
    {
        RoundStateMachine machine = new();
        machine.RestoreFrom(OrchestratorState.FreshBoot(0, 0) with { State = BifrostState.Aborted });

        RoundStateMachine.Outcome outcome = machine.TryApply(
            BuildCommand(McCommand.CommandOneofCase.EventEnd));

        Assert.True(outcome.Success);
        Assert.Equal(BifrostState.Aborted, machine.Current);
        Assert.False(outcome.StateChanged);
    }

    /// <summary>
    /// ApplyBlock (heartbeat-lost seam) rejects subsequent transition
    /// commands with a "transitions blocked" prefix; Resume clears both
    /// Blocked and Paused; a subsequent transition succeeds.
    /// </summary>
    [Fact]
    public void ApplyBlock_RejectsTransitionCommands_PermitsResume()
    {
        RoundStateMachine machine = new();
        machine.RestoreFrom(OrchestratorState.FreshBoot(0, 0) with { State = BifrostState.RoundOpen });

        bool applied = machine.ApplyBlock("heartbeat_lost");
        Assert.True(applied);
        Assert.True(machine.Blocked);
        Assert.True(machine.Paused);
        Assert.Equal("heartbeat_lost", machine.BlockedReason);

        // Gate blocked while heartbeat is lost.
        RoundStateMachine.Outcome gateOutcome = machine.TryApply(
            BuildCommand(McCommand.CommandOneofCase.Gate));
        Assert.False(gateOutcome.Success);
        Assert.Contains("transitions blocked", gateOutcome.Message);
        Assert.Equal(BifrostState.RoundOpen, machine.Current);

        // Resume clears Blocked + Paused in a single command.
        RoundStateMachine.Outcome resumeOutcome = machine.TryApply(
            BuildCommand(McCommand.CommandOneofCase.Resume));
        Assert.True(resumeOutcome.Success);
        Assert.False(machine.Blocked);
        Assert.False(machine.Paused);
        Assert.True(resumeOutcome.FlagsChanged);

        // Subsequent Gate is accepted.
        RoundStateMachine.Outcome gateAgain = machine.TryApply(
            BuildCommand(McCommand.CommandOneofCase.Gate));
        Assert.True(gateAgain.Success);
        Assert.Equal(BifrostState.Gate, machine.Current);
    }

    /// <summary>
    /// ApplyBlock is idempotent: calling twice with the same reason returns
    /// false the second time and doesn't reset the reason.
    /// </summary>
    [Fact]
    public void ApplyBlock_IdempotentOnSameReason()
    {
        RoundStateMachine machine = new();
        machine.RestoreFrom(OrchestratorState.FreshBoot(0, 0) with { State = BifrostState.RoundOpen });

        Assert.True(machine.ApplyBlock("heartbeat_lost"));
        Assert.False(machine.ApplyBlock("heartbeat_lost"));
        Assert.Equal("heartbeat_lost", machine.BlockedReason);
    }

    /// <summary>
    /// Build a well-formed McCommand with the specified oneof variant set.
    /// Default values are chosen to satisfy the 147-cell matrix - business
    /// logic validation (confirm=true, non-empty reason, etc.) is the
    /// gRPC handler's responsibility, not the state machine's.
    /// </summary>
    private static McCommand BuildCommand(McCommand.CommandOneofCase variant)
    {
        McCommand cmd = new() { OperatorHost = "test-host", Confirm = true };
        switch (variant)
        {
            case McCommand.CommandOneofCase.AuctionOpen:
                cmd.AuctionOpen = new AuctionOpenCmd { RoundNumber = 1 };
                break;
            case McCommand.CommandOneofCase.AuctionClose:
                cmd.AuctionClose = new AuctionCloseCmd { RoundNumber = 1 };
                break;
            case McCommand.CommandOneofCase.RoundStart:
                cmd.RoundStart = new RoundStartCmd { RoundNumber = 1 };
                break;
            case McCommand.CommandOneofCase.RoundEnd:
                cmd.RoundEnd = new RoundEndCmd { RoundNumber = 1 };
                break;
            case McCommand.CommandOneofCase.Gate:
                cmd.Gate = new GateCmd { RoundNumber = 1 };
                break;
            case McCommand.CommandOneofCase.Settle:
                cmd.Settle = new SettleCmd { RoundNumber = 1 };
                break;
            case McCommand.CommandOneofCase.NextRound:
                cmd.NextRound = new NextRoundCmd();
                break;
            case McCommand.CommandOneofCase.Pause:
                cmd.Pause = new PauseCmd();
                break;
            case McCommand.CommandOneofCase.Resume:
                cmd.Resume = new ResumeCmd();
                break;
            case McCommand.CommandOneofCase.Abort:
                cmd.Abort = new AbortCmd { Reason = "test" };
                break;
            case McCommand.CommandOneofCase.ForecastRevise:
                cmd.ForecastRevise = new ForecastReviseCmd();
                break;
            case McCommand.CommandOneofCase.RegimeForce:
                cmd.RegimeForce = new RegimeForceCmd();
                break;
            case McCommand.CommandOneofCase.NewsFire:
                cmd.NewsFire = new NewsFireCmd { LibraryKey = "test" };
                break;
            case McCommand.CommandOneofCase.NewsPublish:
                cmd.NewsPublish = new NewsPublishCmd { Text = "test" };
                break;
            case McCommand.CommandOneofCase.AlertUrgent:
                cmd.AlertUrgent = new AlertUrgentCmd { Text = "test" };
                break;
            case McCommand.CommandOneofCase.PhysicalShock:
                cmd.PhysicalShock = new PhysicalShockCmd();
                break;
            case McCommand.CommandOneofCase.TeamKick:
                cmd.TeamKick = new TeamKickCmd { TeamName = "x" };
                break;
            case McCommand.CommandOneofCase.TeamReset:
                cmd.TeamReset = new TeamResetCmd { TeamName = "x" };
                break;
            case McCommand.CommandOneofCase.ConfigSet:
                cmd.ConfigSet = new ConfigSetCmd { Path = "x", Value = "y" };
                break;
            case McCommand.CommandOneofCase.LeaderboardReveal:
                cmd.LeaderboardReveal = new LeaderboardRevealCmd();
                break;
            case McCommand.CommandOneofCase.EventEnd:
                cmd.EventEnd = new EventEndCmd();
                break;
        }

        return cmd;
    }
}
