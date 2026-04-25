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
using ProtoState = Bifrost.Contracts.Round.State;

namespace Bifrost.Orchestrator.Tests.Integration;

/// <summary>
/// End-to-end Abort flow gate covering Requirement 12:
/// <list type="number">
///   <item>Abort from RoundOpen transitions the state machine to Aborted.</item>
///   <item>Exactly one envelope lands on
///         <c>bifrost.round.v1/round.state.aborted</c> carrying the abort
///         reason in the payload.</item>
///   <item>Exactly one envelope lands on
///         <c>bifrost.events.v1/events.market_alert</c> with
///         <c>severity="urgent"</c> and a text containing "aborted" + the
///         abort reason.</item>
///   <item>AuctionOpen from Aborted is rejected as an illegal transition.</item>
///   <item>NextRound from Aborted succeeds and returns the machine to
///         IterationOpen.</item>
/// </list>
/// </summary>
public sealed class AbortFlowTests : IAsyncDisposable
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _tempDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"bifrost-orch-abort-{Guid.NewGuid():N}")).FullName;

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
    public async Task AbortFromRoundOpen_EmitsStatePublish_AndMarketAlert_ThenNextRoundRestoresIteration()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        string statePath = Path.Combine(_tempDir, "state.json");
        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            StatePath = statePath,
            MasterSeed = 99,
        });

        // Pre-seed RoundOpen round_number=2 (in production this would arrive
        // via AuctionOpen → AuctionClose → RoundStart; pre-seeding the
        // snapshot avoids replaying that scaffolding here).
        JsonStateStore seedStore = new(opts, NullLogger<JsonStateStore>.Instance);
        await seedStore.SaveAsync(
            OrchestratorState.FreshBoot(masterSeed: 99, nowNs: 100L) with
            {
                State = BifrostState.RoundOpen,
                RoundNumber = 2,
            },
            cts.Token);

        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        RoundStateMachine machine = new();
        JsonStateStore store = new(opts, NullLogger<JsonStateStore>.Instance);
        OrchestratorRabbitMqTopology topology = new(harness.Channel);
        OrchestratorPublisher publisher = new(harness.Channel, clock);
        RoundStateRingBuffer ring = new();
        RoundSeedAllocator seedAllocator = new(masterSeed: 99);
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

        // Wait for the reconciliation publish to land on the harness so we can
        // exclude it from the Abort assertions below.
        int reconciliationCutoff = 0;
        for (int i = 0; i < 100 && reconciliationCutoff == 0; i++)
        {
            reconciliationCutoff = harness.Captured.Count(c =>
                c.RoutingKey.StartsWith("round.state.", StringComparison.Ordinal));
            if (reconciliationCutoff == 0)
            {
                await Task.Delay(20, cts.Token);
            }
        }

        Assert.Equal(1, reconciliationCutoff);

        // Send Abort.
        TaskCompletionSource<McCommandResult> abortTcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    Abort = new AbortCmd { Reason = "quorum lost" },
                },
                Tcs: abortTcs,
                SourceTag: "abort-flow-test"),
            cts.Token);

        McCommandResult abortResult = await abortTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            cts.Token);

        Assert.True(abortResult.Success);
        Assert.Equal(ProtoState.Aborted, abortResult.NewState.State);

        // Round-state-aborted publish: exactly one, regardless of the
        // pre-existing reconciliation entry that landed earlier.
        IReadOnlyList<CapturedPublish> abortedPublishes = harness.Captured
            .Where(c => c.RoutingKey == "round.state.aborted")
            .ToList();
        CapturedPublish abortPublish = Assert.Single(abortedPublishes);

        Envelope<RoundStateChangedPayload>? abortedEnv = JsonSerializer
            .Deserialize<Envelope<RoundStateChangedPayload>>(
                abortPublish.BodyAsString,
                CamelCaseOptions);
        Assert.NotNull(abortedEnv);
        Assert.Equal("Aborted", abortedEnv!.Payload.State);
        Assert.Equal("quorum lost", abortedEnv.Payload.AbortReason);

        // MarketAlert publish: exactly one, urgent severity, text mentions
        // both "aborted" and the abort reason.
        IReadOnlyList<CapturedPublish> alertPublishes = harness.Captured
            .Where(c => c.RoutingKey == "events.market_alert")
            .ToList();
        CapturedPublish alertPublish = Assert.Single(alertPublishes);

        Envelope<MarketAlertPayload>? alertEnv = JsonSerializer
            .Deserialize<Envelope<MarketAlertPayload>>(
                alertPublish.BodyAsString,
                CamelCaseOptions);
        Assert.NotNull(alertEnv);
        Assert.Equal("urgent", alertEnv!.Payload.Severity);
        Assert.Contains("aborted", alertEnv.Payload.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quorum lost", alertEnv.Payload.Text, StringComparison.OrdinalIgnoreCase);

        // Illegal transition from Aborted (AuctionOpen).
        TaskCompletionSource<McCommandResult> illegalTcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    AuctionOpen = new AuctionOpenCmd { RoundNumber = 3 },
                },
                Tcs: illegalTcs,
                SourceTag: "abort-flow-test"),
            cts.Token);

        McCommandResult illegalResult = await illegalTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            cts.Token);

        Assert.False(illegalResult.Success);
        Assert.Contains("illegal transition", illegalResult.Message, StringComparison.OrdinalIgnoreCase);

        // NextRound is the only legal transition from Aborted.
        TaskCompletionSource<McCommandResult> nextTcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    NextRound = new NextRoundCmd(),
                },
                Tcs: nextTcs,
                SourceTag: "abort-flow-test"),
            cts.Token);

        McCommandResult nextResult = await nextTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            cts.Token);

        Assert.True(nextResult.Success);
        Assert.Equal(ProtoState.IterationOpen, nextResult.NewState.State);

        channel.Writer.Complete();
        await actor.StopAsync(cts.Token);
    }
}
