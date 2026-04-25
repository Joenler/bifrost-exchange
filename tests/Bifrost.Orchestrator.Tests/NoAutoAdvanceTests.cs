using System.Threading.Channels;
using Bifrost.Orchestrator;
using Bifrost.Orchestrator.Actor;
using Bifrost.Orchestrator.Grpc;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.State;
using Bifrost.Orchestrator.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using BifrostState = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.Orchestrator.Tests;

/// <summary>
/// Falsifiable regression gate covering the no-auto-advance requirement: a
/// 30-minute virtual hold (driven via <see cref="FakeClock"/> over a real
/// wall-clock of milliseconds) parked in each of the six transition-holding
/// states produces exactly ONE <c>round.state.*</c> publish — the boot-time
/// reconciliation envelope. No auto-advance code path can pass this test:
/// any timer-driven transition would surface an additional
/// <c>round.state.*</c> publish during the hold.
/// </summary>
/// <remarks>
/// States covered (per the orchestrator phase specification): AuctionOpen,
/// AuctionClosed, RoundOpen, Gate, Settled, Aborted. <c>IterationOpen</c> is
/// intentionally NOT covered — the iteration-seed rotation timer is a
/// non-transition timer that publishes refreshed-iteration-seed envelopes
/// while in IterationOpen, which would be a false positive here.
///
/// Tagged <c>[Trait("Long","true")]</c> so the default CI matrix slot
/// (<c>--filter Trait!=Long</c>) skips them; the long-suite slot
/// (<c>--filter Trait=Long=true</c>) runs them. Default CI does not see this
/// fixture, so the per-row wall-clock cost (~3s) does not regress fast feedback.
///
/// Wall-clock: each row drives the FakeClock 30 minutes virtual via 30 × 1-min
/// advances interleaved with 20ms real-time waits to let the actor's drain
/// loop observe any (hypothetical) auto-advance enqueue. Total per-row real
/// wall-clock ≈ 3 seconds (boot + reconciliation wait + 30 × 20ms advance loop
/// + StopAsync). 6 rows ≈ 18 seconds aggregate.
/// </remarks>
public sealed class NoAutoAdvanceTests : IAsyncDisposable
{
    private readonly string _tempDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"bifrost-orch-noauto-{Guid.NewGuid():N}")).FullName;

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

    public static IEnumerable<TheoryDataRow<BifrostState>> HoldStates()
    {
        yield return new TheoryDataRow<BifrostState>(BifrostState.AuctionOpen);
        yield return new TheoryDataRow<BifrostState>(BifrostState.AuctionClosed);
        yield return new TheoryDataRow<BifrostState>(BifrostState.RoundOpen);
        yield return new TheoryDataRow<BifrostState>(BifrostState.Gate);
        yield return new TheoryDataRow<BifrostState>(BifrostState.Settled);
        yield return new TheoryDataRow<BifrostState>(BifrostState.Aborted);
    }

    [Theory]
    [Trait("Long", "true")]
    [MemberData(nameof(HoldStates))]
    public async Task HoldInState_For30FakeMinutes_ProducesOnlyReconciliationPublish(BifrostState state)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        // Pre-seed the persisted snapshot at the requested hold state. Master
        // seed is irrelevant here; the test never exercises seed allocation.
        string statePath = Path.Combine(_tempDir, $"state-{state}.json");
        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            StatePath = statePath,
            MasterSeed = 0,
            // 1 hour rotation interval prevents iteration-seed timer from
            // firing inside the 30-min virtual hold. The timer is not even
            // started in this test (we only run the actor), but setting the
            // option defensively makes the configuration self-documenting.
            IterationSeedRotationSeconds = 3600,
        });

        JsonStateStore seedStore = new(opts, NullLogger<JsonStateStore>.Instance);
        await seedStore.SaveAsync(
            OrchestratorState.FreshBoot(masterSeed: 0, nowNs: 100L) with
            {
                State = state,
                RoundNumber = 1,
            },
            cts.Token);

        // Build the actor against the in-memory subscriber harness.
        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        RoundStateMachine machine = new();
        JsonStateStore store = new(opts, NullLogger<JsonStateStore>.Instance);
        OrchestratorRabbitMqTopology topology = new(harness.Channel);
        OrchestratorPublisher publisher = new(harness.Channel, clock);
        RoundStateRingBuffer ring = new();
        RoundSeedAllocator seedAllocator = new(masterSeed: 0);
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

        // Wait for the boot-time reconciliation publish to land. The actor
        // publishes BEFORE entering its drain loop, so the envelope appears
        // within a few ms of StartAsync.
        int reconciliationCount = 0;
        for (int i = 0; i < 100 && reconciliationCount == 0; i++)
        {
            reconciliationCount = harness.Captured.Count(c =>
                c.RoutingKey.StartsWith("round.state.", StringComparison.Ordinal));
            if (reconciliationCount == 0)
            {
                await Task.Delay(20, cts.Token);
            }
        }

        Assert.Equal(1, reconciliationCount);

        // Establish the post-reconciliation cutoff. Everything after this index
        // would be an unwarranted publish — the no-auto-advance assertion.
        int cutoffIndex = harness.Captured.Count;

        // Drive 30 fake-minutes in 1-min increments, pausing 20ms each cycle
        // to give the actor's drain loop wall-clock to observe any
        // (hypothetical) auto-advance message that a regression might enqueue.
        for (int minute = 0; minute < 30; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20, cts.Token);
        }

        // Assertion: zero round.state.* publishes after the reconciliation
        // cutoff. Filter on routing-key prefix so per-test fixture artefacts
        // (e.g. mc.command.* audit publishes) do not confound the gate.
        IReadOnlyList<CapturedPublish> postCutoffPublishes = harness.Captured
            .Skip(cutoffIndex)
            .Where(c => c.RoutingKey.StartsWith("round.state.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(postCutoffPublishes);

        channel.Writer.Complete();
        await actor.StopAsync(cts.Token);
    }
}
