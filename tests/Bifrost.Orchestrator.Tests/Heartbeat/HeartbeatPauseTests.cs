using System.Threading.Channels;
using Bifrost.Contracts.Mc;
using Bifrost.Orchestrator;
using Bifrost.Orchestrator.Actor;
using Bifrost.Orchestrator.Grpc;
using Bifrost.Orchestrator.Heartbeat;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.State;
using Bifrost.Orchestrator.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using BifrostState = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.Orchestrator.Tests.Heartbeat;

/// <summary>
/// End-to-end heartbeat-loss / Resume gate covering SPEC Requirement 11:
/// <list type="number">
///   <item>OnChange(false) at RoundOpen: persisted snapshot becomes
///         Blocked=true + Paused=true with reason "heartbeat_lost".</item>
///   <item>A subsequent Gate command is rejected with
///         "transitions blocked: heartbeat_lost" — state machine's Blocked
///         gate fires before the legal-transition matrix lookup.</item>
///   <item>OnChange(true) does NOT auto-clear Blocked — the actor logs the
///         restore but leaves the persisted flags untouched.</item>
///   <item>An MC Resume command clears both Paused and Blocked flags.</item>
///   <item>The previously-rejected Gate command now succeeds (state machine
///         transitions RoundOpen → Gate).</item>
/// </list>
/// </summary>
/// <remarks>
/// Drives the real <see cref="HeartbeatToleranceMonitor"/> against a
/// <see cref="ManualHeartbeatSource"/> test double — the monitor's
/// OnChange-subscribe path enqueues HeartbeatChangeMessage onto the actor
/// channel exactly as the production
/// <see cref="RabbitMqGatewayHeartbeatSource"/> would. The monitor's
/// 1-second poll cadence is bypassed by the synchronous OnChange handler so
/// the test wall-clock stays bounded.
/// </remarks>
public sealed class HeartbeatPauseTests : IAsyncDisposable
{
    private readonly string _tempDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"bifrost-orch-hb-{Guid.NewGuid():N}")).FullName;

    public async ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort temp-dir cleanup
        }

        await ValueTask.CompletedTask;
    }

    /// <summary>
    /// Test-only <see cref="IGatewayHeartbeatSource"/> that exposes a
    /// manual <see cref="Raise"/> entry-point. Mirrors the
    /// <c>InMemoryRoundStateSource</c> pattern from Phase 02 — flips
    /// <see cref="IsHealthy"/> AND raises <see cref="OnChange"/> in a single
    /// synchronous call so the actor's drain loop observes the transition
    /// before the test asserts.
    /// </summary>
    private sealed class ManualHeartbeatSource : IGatewayHeartbeatSource
    {
        private bool _healthy = true;

        public bool IsHealthy => _healthy;

        public event EventHandler<GatewayHeartbeatChanged>? OnChange;

        public void Raise(bool healthy)
        {
            _healthy = healthy;
            OnChange?.Invoke(this, new GatewayHeartbeatChanged(healthy, TimestampNs: 1_000_000L));
        }
    }

    [Fact]
    public async Task HeartbeatLoss_BlocksGate_RestoreDoesNotAutoClear_Resume_UnblocksGate()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        // Pre-seed RoundOpen round_number=2 so the Gate command path exists
        // (Gate is the legal transition from RoundOpen). Avoids replaying the
        // AuctionOpen → AuctionClose → RoundStart scaffolding.
        string statePath = Path.Combine(_tempDir, "state.json");
        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            StatePath = statePath,
            MasterSeed = 7,
        });

        JsonStateStore seedStore = new(opts, NullLogger<JsonStateStore>.Instance);
        await seedStore.SaveAsync(
            OrchestratorState.FreshBoot(masterSeed: 7, nowNs: 100L) with
            {
                State = BifrostState.RoundOpen,
                RoundNumber = 2,
            },
            cts.Token);

        // Build the actor + monitor against a manual heartbeat source.
        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        RoundStateMachine machine = new();
        JsonStateStore store = new(opts, NullLogger<JsonStateStore>.Instance);
        OrchestratorRabbitMqTopology topology = new(harness.Channel);
        OrchestratorPublisher publisher = new(harness.Channel, clock);
        RoundStateRingBuffer ring = new();
        RoundSeedAllocator seedAllocator = new(masterSeed: 7);
        EmptyNewsLibrary newsLibrary = new();

        Channel<IOrchestratorMessage> channel = Channel.CreateBounded<IOrchestratorMessage>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        OrchestratorActor actor = new(
            channel.Reader,
            machine,
            store,
            publisher,
            topology,
            clock,
            opts,
            ring,
            seedAllocator,
            newsLibrary,
            NullLogger<OrchestratorActor>.Instance);

        ManualHeartbeatSource hbSource = new();
        HeartbeatToleranceMonitor monitor = new(
            hbSource,
            channel.Writer,
            clock,
            NullLogger<HeartbeatToleranceMonitor>.Instance);

        await actor.StartAsync(cts.Token);
        await monitor.StartAsync(cts.Token);

        // Wait for the boot reconciliation publish to land before driving the
        // heartbeat loss — keeps the assertion sequence linear.
        await WaitForReconciliationPublishAsync(harness, cts.Token);

        // === Step 1: heartbeat loss → Blocked=true + Paused=true ===
        hbSource.Raise(healthy: false);

        // Drain the actor channel: the OnChange handler enqueues
        // HeartbeatChangeMessage and the actor's HandleHeartbeatChangeAsync
        // persists + publishes. Poll until we observe the persisted Blocked
        // flag rather than sleeping a fixed wall-clock duration.
        OrchestratorState? persisted = await PollForBlockedAsync(store, expected: true, cts.Token);
        Assert.NotNull(persisted);
        Assert.True(persisted!.Blocked, "Blocked must be true after heartbeat loss");
        Assert.Equal("heartbeat_lost", persisted.BlockedReason);
        Assert.True(persisted.Paused, "Paused must be true after heartbeat loss");
        Assert.Equal("heartbeat_lost", persisted.PausedReason);
        Assert.Equal(BifrostState.RoundOpen, persisted.State);

        // === Step 2: Gate command rejected with "transitions blocked" ===
        TaskCompletionSource<McCommandResult> gateBlockedTcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    Gate = new GateCmd { RoundNumber = 2 },
                },
                Tcs: gateBlockedTcs,
                SourceTag: "heartbeat-pause-test"),
            cts.Token);

        McCommandResult gateBlocked = await gateBlockedTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            cts.Token);

        Assert.False(gateBlocked.Success, "Gate must be rejected while Blocked=true");
        Assert.Contains(
            "transitions blocked",
            gateBlocked.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "heartbeat_lost",
            gateBlocked.Message,
            StringComparison.OrdinalIgnoreCase);

        // === Step 3: heartbeat restored → does NOT auto-clear Blocked ===
        hbSource.Raise(healthy: true);

        // Give the drain loop a window to process the healthy=true message;
        // the actor's handler logs but does not mutate state, so the persisted
        // snapshot must remain Blocked.
        await Task.Delay(200, cts.Token);

        OrchestratorState? afterRestored = store.TryLoad();
        Assert.NotNull(afterRestored);
        Assert.True(
            afterRestored!.Blocked,
            "Blocked must persist across heartbeat restore — only MC Resume clears it");
        Assert.Equal("heartbeat_lost", afterRestored.BlockedReason);
        Assert.True(afterRestored.Paused, "Paused must persist alongside Blocked");

        // === Step 4: MC Resume → clears both flags ===
        TaskCompletionSource<McCommandResult> resumeTcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    Resume = new ResumeCmd(),
                },
                Tcs: resumeTcs,
                SourceTag: "heartbeat-pause-test"),
            cts.Token);

        McCommandResult resume = await resumeTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            cts.Token);

        Assert.True(resume.Success, $"Resume must succeed; got: {resume.Message}");

        OrchestratorState? afterResume = store.TryLoad();
        Assert.NotNull(afterResume);
        Assert.False(afterResume!.Blocked, "Resume must clear Blocked");
        Assert.Null(afterResume.BlockedReason);
        Assert.False(afterResume.Paused, "Resume must clear Paused");
        Assert.Null(afterResume.PausedReason);
        Assert.Equal(BifrostState.RoundOpen, afterResume.State);

        // === Step 5: Gate now succeeds → state machine transitions to Gate ===
        TaskCompletionSource<McCommandResult> gateOkTcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    Gate = new GateCmd { RoundNumber = 2 },
                },
                Tcs: gateOkTcs,
                SourceTag: "heartbeat-pause-test"),
            cts.Token);

        McCommandResult gateOk = await gateOkTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            cts.Token);

        Assert.True(gateOk.Success, $"Gate must succeed after Resume; got: {gateOk.Message}");

        OrchestratorState? afterGate = store.TryLoad();
        Assert.NotNull(afterGate);
        Assert.Equal(BifrostState.Gate, afterGate!.State);

        channel.Writer.Complete();
        await monitor.StopAsync(cts.Token);
        await actor.StopAsync(cts.Token);
    }

    /// <summary>
    /// Block until either (a) the persisted snapshot's <c>Blocked</c> flag
    /// matches <paramref name="expected"/> or (b) cancellation fires. Uses a
    /// 25ms poll cadence to keep the wall-clock bounded under CI load.
    /// </summary>
    private static async Task<OrchestratorState?> PollForBlockedAsync(
        JsonStateStore store,
        bool expected,
        CancellationToken ct)
    {
        for (int i = 0; i < 200; i++)
        {
            OrchestratorState? snapshot = store.TryLoad();
            if (snapshot is not null && snapshot.Blocked == expected)
            {
                return snapshot;
            }

            await Task.Delay(25, ct);
        }

        return store.TryLoad();
    }

    /// <summary>
    /// Wait for the actor's boot-time reconciliation publish to land on the
    /// harness. The actor publishes BEFORE entering its drain loop, so the
    /// envelope appears within a few ms of <c>StartAsync</c>.
    /// </summary>
    private static async Task WaitForReconciliationPublishAsync(
        RabbitMqSubscriberHarness harness,
        CancellationToken ct)
    {
        for (int i = 0; i < 100; i++)
        {
            bool reconciliationLanded = harness.Captured.Any(c =>
                c.RoutingKey.StartsWith("round.state.", StringComparison.Ordinal));
            if (reconciliationLanded)
            {
                return;
            }

            await Task.Delay(20, ct);
        }
    }
}
