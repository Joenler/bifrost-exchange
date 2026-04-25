using System.Threading.Channels;
using Bifrost.Orchestrator.Actor;
using Bifrost.Orchestrator.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Orchestrator.Tests.Actor;

/// <summary>
/// Cadence gate for <see cref="IterationSeedTimer"/>: a 10-minute virtual
/// window driven by <see cref="FakeClock"/> at 120-second
/// <c>IterationSeedRotationSeconds</c> produces approximately 5 enqueued
/// <see cref="IterationSeedTickMessage"/> envelopes (allowing real-time
/// scheduling jitter for the timer's <c>Task.Delay(1s)</c> wake-up loop).
/// </summary>
/// <remarks>
/// The timer uses a real <c>Task.Delay(1s)</c> for its wall-clock wake-up
/// but gates the logical tick on <c>IClock.GetUtcNow()</c> (CONTEXT D-22 +
/// RESEARCH Pitfall 4 — pure-PeriodicTimer with FakeTimeProvider hangs).
/// Purely-fake-time testing would require replacing <c>Task.Delay</c> with
/// an injected <c>IDelayProvider</c>; deferred per the plan note.
///
/// The test advances the FakeClock by 60 fake-seconds every real ~1.1s and
/// expects 5 ticks across 10 advances (600 fake-seconds at 120s cadence).
/// The 4-6 inclusive range accounts for the real-time variance window:
/// the timer's last advance may or may not be observed before
/// <c>StopAsync</c> drains the channel.
/// </remarks>
public sealed class IterationSeedRotationTests
{
    [Fact]
    public async Task TenMinuteWindow_AtTwoMinuteCadence_Produces_About_Five_Ticks()
    {
        FakeClock clock = new();
        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            IterationSeedRotationSeconds = 120,
        });
        Channel<IOrchestratorMessage> channel = Channel.CreateUnbounded<IOrchestratorMessage>();

        IterationSeedTimer timer = new(
            channel.Writer,
            clock,
            opts,
            NullLogger<IterationSeedTimer>.Instance);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        await timer.StartAsync(cts.Token);

        // Allow ExecuteAsync to enter its Task.Delay(1s) loop.
        await Task.Delay(50, cts.Token);

        // Advance the FakeClock 60 fake-seconds × 10 = 600 fake-seconds.
        // The timer wakes every real ~1s and observes the fake-clock delta;
        // at 120s rotationInterval, we expect 5 ticks across 600s of advance.
        for (int i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(1100, cts.Token);
        }

        await timer.StopAsync(CancellationToken.None);
        channel.Writer.Complete();

        List<IterationSeedTickMessage> ticks = new();
        await foreach (IOrchestratorMessage msg in channel.Reader.ReadAllAsync(cts.Token))
        {
            if (msg is IterationSeedTickMessage tick)
            {
                ticks.Add(tick);
            }
        }

        // Expected 5 ticks; 4-6 tolerates real-time scheduling jitter on the
        // 11-second test runtime (the final 60s advance may or may not be
        // observed before StopAsync).
        Assert.InRange(ticks.Count, 4, 6);
    }

    [Fact]
    public async Task RotationSecondsOne_FastCadence_EnqueuesAtLeastOneTick()
    {
        // Smoke-level coverage: with rotationSeconds=1, the timer's IClock
        // gate trips on every wake. After ~3s of real time + matching fake
        // advances, at least one tick is enqueued. Validates the timer's
        // happy-path enqueue without committing 10s+ of wall-clock to the
        // 120s-cadence determinism check.
        FakeClock clock = new();
        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            IterationSeedRotationSeconds = 1,
        });
        Channel<IOrchestratorMessage> channel = Channel.CreateUnbounded<IOrchestratorMessage>();

        IterationSeedTimer timer = new(
            channel.Writer,
            clock,
            opts,
            NullLogger<IterationSeedTimer>.Instance);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        await timer.StartAsync(cts.Token);
        await Task.Delay(50, cts.Token);

        // Advance fake clock past rotationSeconds and let real Task.Delay(1s)
        // wake the timer at least once.
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(1100, cts.Token);
        }

        await timer.StopAsync(CancellationToken.None);
        channel.Writer.Complete();

        int tickCount = 0;
        await foreach (IOrchestratorMessage msg in channel.Reader.ReadAllAsync(cts.Token))
        {
            if (msg is IterationSeedTickMessage)
            {
                tickCount++;
            }
        }

        Assert.True(tickCount >= 1, $"expected at least 1 tick, got {tickCount}");
    }
}
