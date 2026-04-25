using System.Threading.Channels;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.Orchestrator.Actor;

/// <summary>
/// IClock-polling BackgroundService that enqueues
/// <see cref="IterationSeedTickMessage"/> envelopes onto the orchestrator's
/// actor channel every <c>IterationSeedRotationSeconds</c> (default 300).
/// </summary>
/// <remarks>
/// Pattern: real <c>Task.Delay(1s)</c> wakes the loop on wall-clock time,
/// then <see cref="IClock.GetUtcNow"/> gates the logical tick. This matches
/// the <c>CommandConsumerService</c> shape and avoids the
/// <c>PeriodicTimer</c> + <c>FakeTimeProvider</c> hang documented in
/// CONTEXT D-22 / RESEARCH Pitfall 4.
///
/// D-22: the timer keeps running under <c>Paused=true</c>. Iteration seeds
/// are public clock-rolls visible on the big screen; freezing them on Pause
/// would break the rolling-seed UX. Pause only halts MC transition commands;
/// the actor's tick handler additionally gates on
/// <c>State == IterationOpen</c> so a tick outside an iteration window is a
/// silent no-op (rotation count + iteration seed only advance during
/// <c>IterationOpen</c>).
///
/// Backpressure: writes to the bounded channel block on <c>WriteAsync</c>
/// when the queue is full (FullMode=Wait). The 256-capacity buffer makes
/// this effectively impossible at the configured 300s cadence; the
/// behaviour is documented for completeness.
/// </remarks>
public sealed class IterationSeedTimer : BackgroundService
{
    private readonly ChannelWriter<IOrchestratorMessage> _writer;
    private readonly IClock _clock;
    private readonly IOptions<OrchestratorOptions> _opts;
    private readonly ILogger<IterationSeedTimer> _logger;

    public IterationSeedTimer(
        ChannelWriter<IOrchestratorMessage> writer,
        IClock clock,
        IOptions<OrchestratorOptions> opts,
        ILogger<IterationSeedTimer> logger)
    {
        _writer = writer;
        _clock = clock;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Floor the configured cadence at 1s so a misconfigured value cannot
        // produce a busy-wait. The default is 300s (5 min) per
        // OrchestratorOptions; production overrides land via appsettings.
        int rotationSeconds = Math.Max(1, _opts.Value.IterationSeedRotationSeconds);
        TimeSpan rotationInterval = TimeSpan.FromSeconds(rotationSeconds);

        DateTimeOffset lastTickUtc = _clock.GetUtcNow();
        _logger.LogInformation(
            "IterationSeedTimer started - rotation={Sec}s, startUtc={Utc}",
            rotationSeconds,
            lastTickUtc);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Real wall-clock wake-up. The 1s poll is fast enough to
                // observe FakeClock advances in tests yet idle enough for
                // production at 300s cadence.
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            DateTimeOffset now = _clock.GetUtcNow();
            if (now - lastTickUtc < rotationInterval)
            {
                continue;
            }

            lastTickUtc = now;
            long tickNs = now.ToUnixTimeMilliseconds() * 1_000_000L;

            try
            {
                await _writer.WriteAsync(new IterationSeedTickMessage(tickNs), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                // Channel completed by the host on shutdown - exit cleanly.
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("IterationSeedTimer stopping");
        await base.StopAsync(cancellationToken);
    }
}
