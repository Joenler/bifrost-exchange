using System.Threading.Channels;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.Imbalance.HostedServices;

/// <summary>
/// Emits a <see cref="ForecastTickMessage"/> every
/// <see cref="ImbalanceSimulatorOptions.TForecastSeconds"/> onto the shared
/// simulator channel. Cadence is driven by <see cref="PeriodicTimer"/> bound to
/// the injected <see cref="TimeProvider"/> so tests substitute
/// <c>FakeTimeProvider</c> and advance virtual time deterministically —
/// <c>FakeTimeProvider.Advance(TimeSpan.FromSeconds(TForecastSeconds))</c> fires
/// exactly one tick. No wall-clock delay in production so scoring cannot
/// drift with real time.
/// <para>
/// Round-state gating lives in the drain loop, not here. The producer is
/// round-agnostic — it emits on every cadence tick regardless of state. The
/// drain loop's <c>HandleForecastTick</c> arm suppresses publication when
/// <c>CurrentRoundState != RoundOpen</c>. This keeps the producer stateless and
/// lets transient-shock expiry still run on every tick (including outside
/// RoundOpen if shocks happened to be mid-window).
/// </para>
/// </summary>
public sealed class ForecastTimerHostedService : BackgroundService
{
    private readonly TimeProvider _timeProvider;
    private readonly Channel<SimulatorMessage> _channel;
    private readonly IClock _clock;
    private readonly IOptions<ImbalanceSimulatorOptions> _options;
    private readonly ILogger<ForecastTimerHostedService> _log;

    public ForecastTimerHostedService(
        TimeProvider timeProvider,
        Channel<SimulatorMessage> channel,
        IClock clock,
        IOptions<ImbalanceSimulatorOptions> options,
        ILogger<ForecastTimerHostedService> log)
    {
        _timeProvider = timeProvider;
        _channel = channel;
        _clock = clock;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cadence = TimeSpan.FromSeconds(_options.Value.TForecastSeconds);
        using var timer = new PeriodicTimer(cadence, _timeProvider);
        _log.LogInformation(
            "ForecastTimerHostedService started; cadence={Seconds}s",
            cadence.TotalSeconds);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                var tsNs = nowMs * 1_000_000L;
                await _channel.Writer.WriteAsync(new ForecastTickMessage(tsNs), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown — do not swallow into the host.
        }
    }
}
