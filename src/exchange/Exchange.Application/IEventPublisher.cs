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
}
