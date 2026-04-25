using System.Threading.Channels;
using Bifrost.Contracts.Mc;
using Bifrost.Orchestrator.Actor;
using Bifrost.Orchestrator.Grpc;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.State;
using Bifrost.Orchestrator.Tests.TestSupport;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using ProtoRoundState = Bifrost.Contracts.Round.RoundState;
using ProtoState = Bifrost.Contracts.Round.State;

namespace Bifrost.Orchestrator.Tests.Integration;

/// <summary>
/// End-to-end gRPC integration gate for Plan 06-07. Exercises
/// <see cref="OrchestratorServiceImpl"/> in-process against a real
/// <see cref="OrchestratorActor"/> drain loop:
/// <list type="number">
///   <item>Valid <c>AuctionOpen</c> from <c>IterationOpen</c> succeeds and
///         transitions the state machine.</item>
///   <item><c>Gate</c> with <c>confirm=false</c> rejects with the typed
///         <c>"confirm required for Gate"</c> message and does NOT mutate
///         state.</item>
///   <item><c>DryRun=true</c> returns success + a populated
///         <c>dry_run_payload</c> without advancing state.</item>
///   <item><c>WatchRoundState(last_seen_transition_ns=0)</c> streams the
///         current snapshot on connect and every subsequent transition.</item>
/// </list>
/// </summary>
public sealed class OrchestratorServiceIntegrationTests : IAsyncDisposable
{
    private readonly string _tempDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"bifrost-orch-int-{Guid.NewGuid():N}")).FullName;

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

    private async Task<Boot> BootAsync(CancellationToken ct)
    {
        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            StatePath = Path.Combine(_tempDir, $"state-{Guid.NewGuid():N}.json"),
            MasterSeed = 7,
        });
        FakeClock clock = new();
        RoundStateRingBuffer ring = new();
        RabbitMqSubscriberHarness harness = new();
        RoundStateMachine machine = new();
        JsonStateStore store = new(opts, NullLogger<JsonStateStore>.Instance);
        OrchestratorRabbitMqTopology topology = new(harness.Channel);
        OrchestratorPublisher publisher = new(harness.Channel, clock);
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

        await actor.StartAsync(ct);

        // Wait for the reconciliation publish so the ring is non-empty
        // when WatchRoundState fresh-connect tests inspect it.
        for (int i = 0; i < 200; i++)
        {
            if (ring.Current() is not null)
            {
                break;
            }
            await Task.Delay(20, ct);
        }

        InMemoryOrchestratorClient client = new(channel.Writer, ring, clock, opts.Value);
        return new Boot(client, channel.Writer, actor);
    }

    private sealed record Boot(
        InMemoryOrchestratorClient Client,
        ChannelWriter<IOrchestratorMessage> Writer,
        OrchestratorActor Actor);

    [Fact]
    public async Task Execute_AuctionOpen_Succeeds_AndTransitions()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        Boot boot = await BootAsync(cts.Token);

        McCommand cmd = new()
        {
            OperatorHost = "mc",
            Confirm = true,
            AuctionOpen = new AuctionOpenCmd { RoundNumber = 1 },
        };

        McCommandResult result = await boot.Client.ExecuteAsync(cmd, cts.Token);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.Equal(ProtoState.AuctionOpen, result.NewState.State);
        // RoundNumber is preserved by Plan 06-06's actor (a follow-up plan
        // wires the round_number-on-AuctionOpen bump). FreshBoot starts with
        // RoundNumber=0, so the first AuctionOpen still carries 0 here.
        Assert.Equal(0, result.NewState.RoundNumber);

        boot.Writer.Complete();
        await boot.Actor.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Execute_Gate_Without_Confirm_Rejects_With_Typed_Message_And_NoStateChange()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        Boot boot = await BootAsync(cts.Token);

        McCommand cmd = new()
        {
            OperatorHost = "mc",
            Confirm = false,
            Gate = new GateCmd { RoundNumber = 1 },
        };

        McCommandResult result = await boot.Client.ExecuteAsync(cmd, cts.Token);

        Assert.False(result.Success);
        Assert.Contains(
            "confirm required for Gate",
            result.Message,
            StringComparison.OrdinalIgnoreCase);

        // State did not advance — the ring's current snapshot is still the
        // boot reconciliation (IterationOpen).
        Assert.Equal("IterationOpen", boot.Client.Ring.Current()!.State);

        boot.Writer.Complete();
        await boot.Actor.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Execute_DryRun_Returns_Preview_Without_StateChange()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        Boot boot = await BootAsync(cts.Token);

        McCommand cmd = new()
        {
            OperatorHost = "mc",
            Confirm = true,
            DryRun = true,
            AuctionOpen = new AuctionOpenCmd { RoundNumber = 1 },
        };

        McCommandResult result = await boot.Client.ExecuteAsync(cmd, cts.Token);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.DryRunPayload));
        Assert.Contains("AuctionOpen", result.DryRunPayload);

        // State must be unchanged: the ring's current snapshot is still
        // IterationOpen because DryRun never enqueues anything.
        Assert.Equal("IterationOpen", boot.Client.Ring.Current()!.State);

        boot.Writer.Complete();
        await boot.Actor.StopAsync(cts.Token);
    }

    [Fact]
    public async Task WatchRoundState_FreshConnect_StreamsCurrent_Then_FutureTransitions()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        Boot boot = await BootAsync(cts.Token);

        List<ProtoRoundState> received = new();
        CaptureStream stream = new(received);

        // Subscribe in the background so the live-tail loop is running when
        // we drive the AuctionOpen transition below.
        using CancellationTokenSource watchCts = CancellationTokenSource
            .CreateLinkedTokenSource(cts.Token);
        Task watchTask = Task.Run(async () =>
        {
            try
            {
                await boot.Client.Impl.WatchRoundState(
                    new WatchRoundStateRequest { LastSeenTransitionNs = 0 },
                    stream,
                    new TestServerCallContext(watchCts.Token));
            }
            catch (OperationCanceledException)
            {
                // expected on tear-down
            }
        }, cts.Token);

        // Give the subscriber a moment to register before we transition.
        for (int i = 0; i < 100 && received.Count == 0; i++)
        {
            await Task.Delay(20, cts.Token);
        }

        // Drive a transition so the subscriber observes a live append.
        McCommandResult result = await boot.Client.ExecuteAsync(
            new McCommand
            {
                OperatorHost = "mc",
                Confirm = true,
                AuctionOpen = new AuctionOpenCmd { RoundNumber = 1 },
            },
            cts.Token);
        Assert.True(result.Success, $"AuctionOpen failed: {result.Message}");

        // Wait for the AuctionOpen snapshot to surface on the stream.
        for (int i = 0; i < 200; i++)
        {
            if (received.Any(r => r.State == ProtoState.AuctionOpen))
            {
                break;
            }
            await Task.Delay(20, cts.Token);
        }

        // Tear down the streaming RPC.
        watchCts.Cancel();
        try
        {
            await watchTask;
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        Assert.True(
            received.Count >= 2,
            $"Expected at least 2 snapshots (current + AuctionOpen transition), "
            + $"got {received.Count}: [{string.Join(", ", received.Select(r => r.State))}]");
        Assert.Equal(ProtoState.IterationOpen, received[0].State);
        Assert.Contains(received, r => r.State == ProtoState.AuctionOpen);

        boot.Writer.Complete();
        await boot.Actor.StopAsync(cts.Token);
    }

    [Fact]
    public async Task WatchRoundState_ResumeFromOlderThanRing_StreamsSyntheticResumeReset()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        Boot boot = await BootAsync(cts.Token);

        // Drive at least one transition so the ring has a non-trivial state.
        McCommandResult auctionResult = await boot.Client.ExecuteAsync(
            new McCommand
            {
                OperatorHost = "mc",
                Confirm = true,
                AuctionOpen = new AuctionOpenCmd { RoundNumber = 1 },
            },
            cts.Token);
        Assert.True(auctionResult.Success, $"AuctionOpen failed: {auctionResult.Message}");

        // Wait for the AuctionOpen snapshot to land in the ring.
        for (int i = 0; i < 200; i++)
        {
            if (boot.Client.Ring.Current()!.State == "AuctionOpen")
            {
                break;
            }
            await Task.Delay(20, cts.Token);
        }

        // Resume from a transition_ns that is OLDER than every snapshot
        // currently in the ring (last_seen=1 — well before the boot
        // reconciliation's transition_ns). Per CONTEXT D-15 + the comment
        // block in mc.proto, the server MUST stream the current snapshot
        // as a synthetic resume-reset rather than rejecting.
        List<ProtoRoundState> received = new();
        CaptureStream stream = new(received);
        using CancellationTokenSource watchCts = CancellationTokenSource
            .CreateLinkedTokenSource(cts.Token);

        Task watchTask = Task.Run(async () =>
        {
            try
            {
                await boot.Client.Impl.WatchRoundState(
                    new WatchRoundStateRequest { LastSeenTransitionNs = 1 },
                    stream,
                    new TestServerCallContext(watchCts.Token));
            }
            catch (OperationCanceledException)
            {
                // expected on tear-down
            }
        }, cts.Token);

        // Wait for the synthetic resume-reset emit.
        for (int i = 0; i < 200 && received.Count == 0; i++)
        {
            await Task.Delay(20, cts.Token);
        }

        watchCts.Cancel();
        try
        {
            await watchTask;
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        // Resume-older-than-ring path: server streams CURRENT (AuctionOpen)
        // as a single synthetic reset and never throws. The exact entry count
        // depends on whether the reconciliation snapshot is still in the
        // 128-capacity ring (it is, after only one extra transition); the
        // server's "older than ring tail" branch streams Current() and the
        // live tail loop emits nothing further before we cancel.
        Assert.True(
            received.Count >= 1,
            $"Expected at least one synthetic reset snapshot, got {received.Count}");
        Assert.Equal(ProtoState.AuctionOpen, received[^1].State);

        boot.Writer.Complete();
        await boot.Actor.StopAsync(cts.Token);
    }

    /// <summary>
    /// Minimal <see cref="IServerStreamWriter{T}"/> that records every
    /// streamed snapshot in call order. Sufficient for assertions on the
    /// initial-replay + live-tail sequence.
    /// </summary>
    private sealed class CaptureStream : IServerStreamWriter<ProtoRoundState>
    {
        private readonly List<ProtoRoundState> _collected;
        private readonly object _gate = new();

        public CaptureStream(List<ProtoRoundState> collected)
        {
            _collected = collected;
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(ProtoRoundState message)
        {
            lock (_gate)
            {
                _collected.Add(message);
            }
            return Task.CompletedTask;
        }
    }
}
