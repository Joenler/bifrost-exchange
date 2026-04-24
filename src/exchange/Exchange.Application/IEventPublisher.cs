namespace Bifrost.Exchange.Application;

public interface IEventPublisher
{
    ValueTask PublishPrivate(string clientId, object @event, string? correlationId = null);
    ValueTask PublishPublicDelta(string instrumentId, object delta, long sequence);
    ValueTask PublishReply(string replyTo, string correlationId, object response);
    ValueTask PublishPublicTrade(string instrumentId, object trade, long sequence);
    ValueTask PublishPublicInstrument(object @event);
    ValueTask PublishPublicOrderStats(string instrumentId, object stats);
    ValueTask PublishPublicSnapshot(string instrumentId, object snapshot, long sequence);

    /// <summary>
    /// Generic public-events payload (events.proto::Event oneof variants such
    /// as RegimeChange, ForecastUpdate, ForecastRevision, ImbalancePrint)
    /// published onto the public topic exchange with a caller-supplied routing
    /// key and message-type discriminator. Quoter, orchestrator, and
    /// imbalance-simulator emissions all flow through here so the wire shape
    /// (envelope + payload) stays consistent.
    /// </summary>
    ValueTask PublishPublicEvent(string routingKey, string messageType, object @event);
}
