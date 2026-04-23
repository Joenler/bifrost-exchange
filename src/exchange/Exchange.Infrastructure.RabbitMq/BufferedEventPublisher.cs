using System.Threading.Channels;
using Bifrost.Exchange.Application;
using Microsoft.Extensions.Logging;

namespace Bifrost.Exchange.Infrastructure.RabbitMq;

public sealed class BufferedEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly RabbitMqEventPublisher _inner;
    private readonly Channel<Func<ValueTask>> _queue;
    private readonly Task _drainTask;
    private readonly ILogger? _logger;

    public BufferedEventPublisher(RabbitMqEventPublisher inner, ILogger? logger = null)
    {
        _inner = inner;
        _logger = logger;
        _queue = Channel.CreateBounded<Func<ValueTask>>(
            new BoundedChannelOptions(8192)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        _drainTask = DrainLoop();
    }

    public ValueTask PublishPrivate(string clientId, object @event, string? correlationId = null)
    {
        _queue.Writer.TryWrite(() => _inner.PublishPrivate(clientId, @event, correlationId));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishReply(string replyTo, string correlationId, object response)
    {
        _queue.Writer.TryWrite(() => _inner.PublishReply(replyTo, correlationId, response));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicTrade(string instrumentId, object trade, long sequence)
    {
        _queue.Writer.TryWrite(() => _inner.PublishPublicTrade(instrumentId, trade, sequence));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicDelta(string instrumentId, object delta, long sequence)
    {
        _queue.Writer.TryWrite(() => _inner.PublishPublicDelta(instrumentId, delta, sequence));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicSnapshot(string instrumentId, object snapshot, long sequence)
    {
        _queue.Writer.TryWrite(() => _inner.PublishPublicSnapshot(instrumentId, snapshot, sequence));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicInstrument(object @event)
    {
        _queue.Writer.TryWrite(() => _inner.PublishPublicInstrument(@event));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicOrderStats(string instrumentId, object stats)
    {
        _queue.Writer.TryWrite(() => _inner.PublishPublicOrderStats(instrumentId, stats));
        return ValueTask.CompletedTask;
    }

    private async Task DrainLoop()
    {
        await foreach (var publish in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await publish();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Buffered publish failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.Complete();
        await _drainTask;
        await _inner.DisposeAsync();
    }
}
