using System.Collections.Concurrent;
using Bifrost.Exchange.Application;

namespace Bifrost.Exchange.Tests.Fixtures;

/// <summary>
/// Test-only <see cref="IEventPublisher"/> that captures every dispatched event into
/// thread-safe queues. Used by SingleWriterStressTests (8-thread Parallel.For) and
/// BookReconstructionTests (ordered-by-sequence delta replay).
///
/// Every queue uses <see cref="ConcurrentQueue{T}"/> so the stress harness can record
/// from 8 producer threads without corrupting state. Each method returns
/// <see cref="ValueTask.CompletedTask"/> — no async work, no ordering guarantee beyond
/// FIFO per-enqueue.
/// </summary>
public sealed class CapturingEventPublisher : IEventPublisher
{
    public ConcurrentQueue<(string ClientId, object Evt, string? CorrelationId)> CapturedPrivate { get; } = new();
    public ConcurrentQueue<(string RoutingKey, object Delta, long Sequence)> CapturedDeltas { get; } = new();
    public ConcurrentQueue<(string ReplyTo, string CorrelationId, object Response)> CapturedReplies { get; } = new();
    public ConcurrentQueue<(string RoutingKey, object Trade, long Sequence)> CapturedTrades { get; } = new();
    public ConcurrentQueue<object> CapturedInstrumentEvents { get; } = new();
    public ConcurrentQueue<(string RoutingKey, object Stats)> CapturedOrderStats { get; } = new();
    public ConcurrentQueue<(string RoutingKey, object Snapshot, long Sequence)> CapturedSnapshots { get; } = new();

    public ValueTask PublishPrivate(string clientId, object @event, string? correlationId = null)
    {
        CapturedPrivate.Enqueue((clientId, @event, correlationId));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicDelta(string instrumentId, object delta, long sequence)
    {
        CapturedDeltas.Enqueue((instrumentId, delta, sequence));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishReply(string replyTo, string correlationId, object response)
    {
        CapturedReplies.Enqueue((replyTo, correlationId, response));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicTrade(string instrumentId, object trade, long sequence)
    {
        CapturedTrades.Enqueue((instrumentId, trade, sequence));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicInstrument(object @event)
    {
        CapturedInstrumentEvents.Enqueue(@event);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicOrderStats(string instrumentId, object stats)
    {
        CapturedOrderStats.Enqueue((instrumentId, stats));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishPublicSnapshot(string instrumentId, object snapshot, long sequence)
    {
        CapturedSnapshots.Enqueue((instrumentId, snapshot, sequence));
        return ValueTask.CompletedTask;
    }
}
