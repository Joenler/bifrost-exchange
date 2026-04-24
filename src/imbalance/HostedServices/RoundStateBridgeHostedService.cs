using System.Threading.Channels;
using Bifrost.Exchange.Application.RoundState;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bifrost.Imbalance.HostedServices;

/// <summary>
/// Adapts <see cref="IRoundStateSource.OnChange"/> onto the simulator's shared
/// <see cref="Channel{T}"/> of <see cref="SimulatorMessage"/> so the actor-loop
/// drain receives <see cref="RoundStateMessage"/> variants through the same
/// single-writer path as fills, shocks, and forecast ticks. Phase 04 tests
/// substitute <c>MockRoundStateSource</c> whose <c>Set</c> method raises
/// <see cref="IRoundStateSource.OnChange"/> synchronously; a future
/// RabbitMQ-backed source (Phase 06 orchestrator) will subscribe to the
/// <c>bifrost.round</c> topic and raise the same event — this bridge flows
/// unchanged across both wirings.
/// <para>
/// Lifetime discipline: the handler is attached in the ctor and detached in
/// <see cref="StopAsync"/> BEFORE delegating to <see cref="BackgroundService.StopAsync"/>.
/// A leaked handler past host shutdown would keep a strong reference to this
/// instance alive on the source's invocation list and continue forwarding into
/// a completed channel — both are silent availability hazards. The attached
/// timing uses the ctor (not <see cref="ExecuteAsync"/>) so transitions raised
/// between <see cref="IHostedService.StartAsync"/> and the first
/// <c>WaitForNextTickAsync</c> are never missed.
/// </para>
/// <para>
/// Back-pressure: <see cref="ChannelWriter{T}.TryWrite"/> is the primary path
/// because <see cref="IRoundStateSource.OnChange"/> fires synchronously on the
/// raising thread and an async <c>WriteAsync</c> would either block that thread
/// or require the handler to become <c>async void</c>. Under normal load the
/// shared channel (bounded at 8192, <c>FullMode=Wait</c>) is nowhere near full
/// so <c>TryWrite</c> succeeds synchronously. The cold-path fallback dispatches
/// an async write onto the thread pool so a saturated channel does not drop a
/// round transition — losing a <see cref="RoundStateMessage"/> would mean the
/// drain loop's round-state gate never flips, stranding fills / forecasts /
/// settlement for the entire round.
/// </para>
/// </summary>
public sealed class RoundStateBridgeHostedService : BackgroundService
{
    private readonly IRoundStateSource _source;
    private readonly Channel<SimulatorMessage> _channel;
    private readonly ILogger<RoundStateBridgeHostedService> _log;

    public RoundStateBridgeHostedService(
        IRoundStateSource source,
        Channel<SimulatorMessage> channel,
        ILogger<RoundStateBridgeHostedService> log)
    {
        _source = source;
        _channel = channel;
        _log = log;

        _source.OnChange += OnRoundStateChanged;
    }

    private void OnRoundStateChanged(object? sender, RoundStateChangedEventArgs e)
    {
        var msg = new RoundStateMessage(e.Previous, e.Current, e.TimestampNs);
        if (_channel.Writer.TryWrite(msg))
        {
            return;
        }

        _log.LogError(
            "Channel TryWrite failed for RoundStateMessage {Previous}->{Current}; falling back to async write",
            e.Previous, e.Current);

        // Cold-path fallback: if the channel is saturated (should not happen on
        // a healthy run), dispatch the write to the thread pool so the round
        // transition is not lost. A lost transition would strand the round-state
        // gate closed across the affected round.
        var writer = _channel.Writer;
        _ = Task.Run(async () =>
        {
            try
            {
                await writer.WriteAsync(msg);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "Async fallback WriteAsync failed for RoundStateMessage {Previous}->{Current}",
                    msg.Previous, msg.Current);
            }
        });
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "RoundStateBridgeHostedService started; forwarding IRoundStateSource.OnChange to simulator channel.");

        // No active work — the event handler drives writes. Park until shutdown.
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _source.OnChange -= OnRoundStateChanged;
        await base.StopAsync(cancellationToken);
    }
}
