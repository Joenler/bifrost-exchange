using System.Collections.Concurrent;
using Bifrost.Exchange.Application;

namespace Bifrost.Imbalance.Tests.Fixtures;

/// <summary>
/// Test-only <see cref="IEventPublisher"/> that captures every dispatched event
/// into thread-safe queues. Mirrors
/// <c>Bifrost.Exchange.Tests.Fixtures.CapturingEventPublisher</c> but adds a
/// <see cref="CapturedPublic"/> queue so emission paths that publish via the
/// generic public-event route (routing-key + message-type) can be asserted
/// alongside the interface-mandated Private / Trade / Delta / Reply / Snapshot
/// / Instrument / OrderStats surfaces.
/// <para>
/// Every queue uses <see cref="ConcurrentQueue{T}"/> so the drain loop and the
/// producer side can publish concurrently without corrupting capture state.
/// Each method returns <see cref="ValueTask.CompletedTask"/> — no async work,
/// no ordering guarantee beyond FIFO per-enqueue.
/// </para>
/// </summary>
public sealed class CapturingEventPublisher : IEventPublisher
{
    public ConcurrentQueue<(string ClientId, object Evt, string? CorrelationId)> CapturedPrivate { get; } = new();
    public ConcurrentQueue<(string InstrumentId, object Delta, long Sequence)> CapturedDeltas { get; } = new();
    public ConcurrentQueue<(string ReplyTo, string CorrelationId, object Response)> CapturedReplies { get; } = new();
    public ConcurrentQueue<(string InstrumentId, object Trade, long Sequence)> CapturedTrades { get; } = new();
    public ConcurrentQueue<object> CapturedInstrumentEvents { get; } = new();
    public ConcurrentQueue<(string InstrumentId, object Stats)> CapturedOrderStats { get; } = new();
    public ConcurrentQueue<(string InstrumentId, object Snapshot, long Sequence)> CapturedSnapshots { get; } = new();

    /// <summary>
    /// Generic public-event capture (routing key + message-type + payload). The
    /// simulator publishes forecast, forecast-revision, and imbalance-print
    /// messages through <c>BufferedEventPublisher.PublishPublicEvent</c>; tests
    /// assert over this queue by routing-key substring.
    /// </summary>
    public ConcurrentQueue<(string RoutingKey, string MessageType, object Evt)> CapturedPublic { get; } = new();

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

    /// <summary>
    /// Generic public-event emission (forecast / forecast-revision / imbalance
    /// print). Matches <c>BufferedEventPublisher.PublishPublicEvent</c>'s shape
    /// even though the method is not on the <see cref="IEventPublisher"/>
    /// interface — callers that want this capture cast to the concrete type
    /// or assign this fixture to a typed field.
    /// </summary>
    public ValueTask PublishPublicEvent(string routingKey, string messageType, object @event)
    {
        CapturedPublic.Enqueue((routingKey, messageType, @event));
        return ValueTask.CompletedTask;
    }
}
