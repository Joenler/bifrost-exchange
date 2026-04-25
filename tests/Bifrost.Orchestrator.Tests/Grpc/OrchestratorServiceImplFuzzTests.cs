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

namespace Bifrost.Orchestrator.Tests.Grpc;

/// <summary>
/// SPEC Req 6 / CONTEXT D-27 fuzz gate: 256 deliberately-malformed
/// <see cref="McCommand"/> payloads produce 256 valid
/// <see cref="McCommandResult"/> responses; ZERO
/// <see cref="RpcException"/> objects are observed client-side.
/// </summary>
/// <remarks>
/// The fuzz exercises the five reject categories in
/// <see cref="OrchestratorServiceImpl.Execute"/>:
/// <list type="number">
///   <item>Missing oneof — no command set</item>
///   <item>Oversized operator_host (>256 chars)</item>
///   <item>Operator_host containing control characters</item>
///   <item>Destructive command (Gate) without confirm=true</item>
///   <item>Valid-but-illegal transition (Gate from IterationOpen)</item>
/// </list>
/// Each iteration uses a deterministic <see cref="Random"/> seed (42) so a
/// failing assertion is reproducible. The test is wall-clock-bounded by a
/// 60-second cancellation token — even a single hanging Execute() call
/// surfaces as a test timeout rather than a hung process.
/// </remarks>
public sealed class OrchestratorServiceImplFuzzTests : IAsyncDisposable
{
    private readonly string _tempDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"bifrost-orch-fuzz-{Guid.NewGuid():N}")).FullName;

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
    public async Task TwoFiftySix_MalformedPayloads_NeverThrow_RpcException()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            StatePath = Path.Combine(_tempDir, "state.json"),
            MasterSeed = 42,
        });

        FakeClock clock = new();
        RoundStateRingBuffer ring = new();
        RabbitMqSubscriberHarness harness = new();
        RoundStateMachine machine = new();
        JsonStateStore store = new(opts, NullLogger<JsonStateStore>.Instance);
        OrchestratorRabbitMqTopology topology = new(harness.Channel);
        OrchestratorPublisher publisher = new(harness.Channel, clock);
        RoundSeedAllocator seedAllocator = new(masterSeed: 42);
        EmptyNewsLibrary newsLibrary = new();

        Channel<IOrchestratorMessage> channel = Channel.CreateBounded<IOrchestratorMessage>(
            new BoundedChannelOptions(512)
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

        // Wait for the reconciliation publish so we know the actor is fully
        // booted before the fuzz starts driving commands.
        for (int i = 0; i < 200; i++)
        {
            if (harness.Captured.Count(c => c.RoutingKey.StartsWith(
                "round.state.",
                StringComparison.Ordinal)) > 0)
            {
                break;
            }
            await Task.Delay(20, cts.Token);
        }

        InMemoryOrchestratorClient client = new(channel.Writer, ring, clock, opts.Value);

        int rejects = 0;
        int accepts = 0;
        int rpcExceptionCount = 0;
        int nullResultCount = 0;
        List<string> firstFiveRejectMessages = new();

        for (int i = 0; i < 256; i++)
        {
            McCommand cmd = BuildMalformedCommand(i);

            McCommandResult? result = null;
            try
            {
                result = await client.ExecuteAsync(cmd, cts.Token);
            }
            catch (RpcException ex)
            {
                rpcExceptionCount++;
                Assert.Fail(
                    $"Fuzz iteration {i} (case {i % 5}): unexpected RpcException "
                    + $"({ex.StatusCode}): {ex.Message}");
                return;
            }

            if (result is null)
            {
                nullResultCount++;
                continue;
            }

            Assert.False(
                string.IsNullOrWhiteSpace(result.Message),
                $"Fuzz iteration {i}: McCommandResult.Message was null/whitespace");

            if (result.Success)
            {
                accepts++;
            }
            else
            {
                rejects++;
                if (firstFiveRejectMessages.Count < 5)
                {
                    firstFiveRejectMessages.Add($"[case {i % 5}] {result.Message}");
                }
            }
        }

        // Invariant assertions — every iteration completed with a typed
        // McCommandResult, none threw RpcException, none came back null.
        Assert.Equal(0, rpcExceptionCount);
        Assert.Equal(0, nullResultCount);
        Assert.Equal(256, rejects + accepts);

        // Coverage check: a healthy fuzz produces both accepts and rejects.
        // The five categories above all reject, so the actual mix is
        // 256 rejects + 0 accepts in the current matrix — but at minimum
        // we want non-zero rejects to prove the validators ran.
        Assert.True(
            rejects > 0,
            $"Expected at least one rejection across 256 malformed payloads. "
            + $"Got accepts={accepts}, rejects={rejects}");

        channel.Writer.Complete();
        await actor.StopAsync(cts.Token);
    }

    /// <summary>
    /// Build a malformed <see cref="McCommand"/> for the given iteration
    /// index. Cycles through the five reject categories in
    /// <see cref="OrchestratorServiceImpl.Execute"/>.
    /// </summary>
    private static McCommand BuildMalformedCommand(int i)
    {
        McCommand cmd = new();
        switch (i % 5)
        {
            case 0:
                // Missing oneof: nothing assigned to the oneof slot.
                cmd.OperatorHost = "test";
                cmd.Confirm = true;
                break;
            case 1:
                // Oversized operator_host (>256 chars). Use a 300-char string
                // so a single off-by-one in the validator surfaces.
                cmd.OperatorHost = new string('x', 300);
                cmd.AuctionOpen = new AuctionOpenCmd { RoundNumber = i };
                break;
            case 2:
                // Control char in operator_host (NUL).
                cmd.OperatorHost = "bad\0host";
                cmd.AuctionOpen = new AuctionOpenCmd { RoundNumber = i };
                break;
            case 3:
                // Destructive without confirm=true: Gate is destructive
                // per IsDestructive, and the validator MUST short-circuit
                // before enqueue.
                cmd.OperatorHost = "mc";
                cmd.Confirm = false;
                cmd.Gate = new GateCmd { RoundNumber = 1 };
                break;
            case 4:
                // Valid-but-illegal transition: Gate is destructive,
                // confirm=true so the validator passes, but Gate is illegal
                // from IterationOpen — RoundStateMachine.TryApply returns a
                // typed reject ("illegal transition: Gate from IterationOpen").
                cmd.OperatorHost = "mc";
                cmd.Confirm = true;
                cmd.Gate = new GateCmd { RoundNumber = 1 };
                break;
        }
        return cmd;
    }
}
