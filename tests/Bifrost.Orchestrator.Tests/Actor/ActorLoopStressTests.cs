using System.Collections.Concurrent;
using System.Threading.Channels;
using Bifrost.Contracts.Mc;
using Bifrost.Orchestrator.Actor;
using Bifrost.Orchestrator.Grpc;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.State;
using Bifrost.Orchestrator.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Orchestrator.Tests.Actor;

/// <summary>
/// Gate on the actor-loop single-writer contract: 1008 concurrent
/// <see cref="McCommandMessage"/> envelopes from 16 threads all complete exactly
/// once, every <see cref="McCommandResult"/> is non-null, and no banned
/// concurrency primitive is used (explicit per-thread <c>new Random(seed)</c>
/// instead of <c>Random.Shared</c>).
/// </summary>
public sealed class ActorLoopStressTests : IAsyncDisposable
{
    private readonly string _tempDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"bifrost-orch-stress-{Guid.NewGuid():N}")).FullName;

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
    public async Task ThousandCmds_SixteenThreads_AllComplete_Exactly_Once()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            StatePath = Path.Combine(_tempDir, "state.json"),
            MasterSeed = 42,
        });
        RoundStateMachine machine = new();
        JsonStateStore store = new(opts, NullLogger<JsonStateStore>.Instance);
        OrchestratorRabbitMqTopology topology = new(harness.Channel);
        OrchestratorPublisher publisher = new(harness.Channel, clock);
        RoundStateRingBuffer ring = new();
        RoundSeedAllocator seedAllocator = new(masterSeed: 42);
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

        const int threadCount = 16;
        const int cmdsPerThread = 63; // 16 × 63 = 1008

        ConcurrentBag<TaskCompletionSource<McCommandResult>> allTcs = new();

        // Parallel.ForEachAsync provides 16-way concurrent producers without
        // the blocking .GetResult() pattern Parallel.For would require (the
        // xUnit1031 analyzer fails the build on blocking waits inside tests).
        await Parallel.ForEachAsync(
            Enumerable.Range(0, threadCount),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = threadCount,
                CancellationToken = cts.Token,
            },
            async (threadIndex, ct) =>
            {
                // Deterministic per-thread seed - matches the Phase 02
                // SingleWriterStressTests discipline (no Random.Shared).
                Random rnd = new(42 + threadIndex);

                for (int i = 0; i < cmdsPerThread; i++)
                {
                    McCommand cmd = new() { OperatorHost = $"t{threadIndex}", Confirm = true };
                    switch (rnd.Next(3))
                    {
                        case 0:
                            cmd.NewsPublish = new NewsPublishCmd { Text = $"msg-{threadIndex}-{i}" };
                            break;
                        case 1:
                            cmd.AlertUrgent = new AlertUrgentCmd { Text = $"alert-{threadIndex}-{i}" };
                            break;
                        default:
                            cmd.ConfigSet = new ConfigSetCmd { Path = "k", Value = "v" };
                            break;
                    }

                    TaskCompletionSource<McCommandResult> tcs = new(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    allTcs.Add(tcs);

                    await channel.Writer.WriteAsync(
                        new McCommandMessage(
                            TsNs: clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                            Cmd: cmd,
                            Tcs: tcs,
                            SourceTag: $"stress-t{threadIndex}"),
                        ct);
                }
            });

        // Wait for every TCS to complete. No polling - each Task signals when
        // the drain loop completes its envelope.
        McCommandResult[] results = await Task.WhenAll(allTcs.Select(t => t.Task))
            .WaitAsync(cts.Token);

        channel.Writer.Complete();
        await actor.StopAsync(cts.Token);

        Assert.Equal(threadCount * cmdsPerThread, results.Length);
        Assert.All(results, r => Assert.NotNull(r));

        // Every TCS completed exactly once - Task.WhenAll would have faulted
        // or hung otherwise.
        Assert.All(allTcs, t => Assert.True(t.Task.IsCompletedSuccessfully));
    }
}
