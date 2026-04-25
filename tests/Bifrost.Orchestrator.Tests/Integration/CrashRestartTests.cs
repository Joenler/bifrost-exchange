using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Contracts.Mc;
using Bifrost.Orchestrator.Actor;
using Bifrost.Orchestrator.Grpc;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.State;
using Bifrost.Orchestrator.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using BifrostState = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.Orchestrator.Tests.Integration;

/// <summary>
/// Crash-restart gate: a persisted RoundOpen { round_number=3, Paused=true }
/// snapshot survives a restart of the orchestrator actor. After restart:
/// <list type="number">
///   <item>The first RoundStateChanged publish has IsReconciliation=true with
///         matching state + round_number + paused flag.</item>
///   <item>A subsequent AuctionOpen command is rejected as an illegal
///         transition from RoundOpen.</item>
/// </list>
/// </summary>
public sealed class CrashRestartTests : IAsyncDisposable
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _tempDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"bifrost-orch-crash-{Guid.NewGuid():N}")).FullName;

    private string StatePath => Path.Combine(_tempDir, "state.json");

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

    [Fact]
    public async Task RestartFromPersistedRoundOpen_Publishes_Reconciliation_With_PausedTrue()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        // Arrange: pre-seed a persisted state file at RoundOpen
        // round_number=3, Paused=true. Simulates a crash AFTER
        // the transition to RoundOpen + the Pause command.
        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            StatePath = StatePath,
            MasterSeed = 1,
        });

        JsonStateStore seedStore = new(opts, NullLogger<JsonStateStore>.Instance);
        OrchestratorState seeded = OrchestratorState.FreshBoot(masterSeed: 1, nowNs: 100L) with
        {
            State = BifrostState.RoundOpen,
            RoundNumber = 3,
            Paused = true,
            PausedReason = "mc",
        };
        await seedStore.SaveAsync(seeded, cts.Token);

        // Act: start a fresh actor against the same state path. The load-or-
        // fresh-boot path in ExecuteAsync picks up the seeded snapshot and
        // fires a reconciliation publish on boot.
        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        RoundStateMachine machine = new();
        JsonStateStore store = new(opts, NullLogger<JsonStateStore>.Instance);
        OrchestratorRabbitMqTopology topology = new(harness.Channel);
        OrchestratorPublisher publisher = new(harness.Channel, clock);
        RoundStateRingBuffer ring = new();
        RoundSeedAllocator seedAllocator = new(masterSeed: 1);
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

        await actor.StartAsync(cts.Token);

        // Poll for the reconciliation publish (it races with the drain-loop
        // await, but ExecuteAsync publishes BEFORE reading the first message,
        // so it should land within a few ms).
        CapturedPublish? reconciliation = null;
        for (int i = 0; i < 100 && reconciliation is null; i++)
        {
            reconciliation = harness.Captured
                .FirstOrDefault(c => c.RoutingKey.StartsWith(
                    "round.state.",
                    StringComparison.Ordinal));
            if (reconciliation is null)
            {
                await Task.Delay(50, cts.Token);
            }
        }

        Assert.NotNull(reconciliation);

        Envelope<RoundStateChangedPayload>? env = JsonSerializer
            .Deserialize<Envelope<RoundStateChangedPayload>>(
                reconciliation!.BodyAsString,
                CamelCaseOptions);
        Assert.NotNull(env);
        Assert.True(env!.Payload.IsReconciliation);
        Assert.Equal("RoundOpen", env.Payload.State);
        Assert.Equal(3, env.Payload.RoundNumber);
        Assert.True(env.Payload.Paused);
        Assert.Equal("round.state.round_open", reconciliation.RoutingKey);

        // AuctionOpen is NOT a legal transition from RoundOpen - expect a
        // typed rejection (success=false, message prefix "illegal transition").
        TaskCompletionSource<McCommandResult> rejectTcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "test",
                    Confirm = true,
                    AuctionOpen = new AuctionOpenCmd { RoundNumber = 4 },
                },
                Tcs: rejectTcs,
                SourceTag: "crash-restart-test"),
            cts.Token);

        McCommandResult rejectResult = await rejectTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            cts.Token);

        Assert.False(rejectResult.Success);
        Assert.Contains("illegal transition", rejectResult.Message, StringComparison.OrdinalIgnoreCase);

        channel.Writer.Complete();
        await actor.StopAsync(cts.Token);
    }
}
