using Bifrost.Exchange.Domain;

namespace Bifrost.Exchange.Application;

public sealed class BookPublisher(
    IEventPublisher publisher,
    PublicSequenceTracker sequenceTracker)
{
    public async ValueTask PublishBookUpdate(
        MatchingEngine engine,
        IReadOnlySet<Price> affectedBidPrices,
        IReadOnlySet<Price> affectedAskPrices,
        InstrumentId instrumentId,
        long timestampNs)
    {
        if (affectedBidPrices.Count == 0 && affectedAskPrices.Count == 0)
            return;

        var routingKey = instrumentId.ToRoutingKey();

        var deltaSeq = sequenceTracker.Next(instrumentId);
        var delta = BookDeltaBuilder.Build(engine.Book, affectedBidPrices, affectedAskPrices, deltaSeq, timestampNs);
        await publisher.PublishPublicDelta(routingKey, delta, deltaSeq);
    }
}
