using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Domain;

namespace Bifrost.Exchange.Application;

public static class BookDeltaBuilder
{
    public static BookDeltaEvent Build(
        OrderBook book,
        IReadOnlySet<Price> affectedBidPrices,
        IReadOnlySet<Price> affectedAskPrices,
        long sequence,
        long timestampNs)
    {
        var changedBids = affectedBidPrices
            .Select(price => BuildLevelDto(book, Side.Buy, price))
            .ToArray();

        var changedAsks = affectedAskPrices
            .Select(price => BuildLevelDto(book, Side.Sell, price))
            .ToArray();

        return new BookDeltaEvent(
            InstrumentIdMapping.ToDto(book.InstrumentId),
            sequence,
            changedBids,
            changedAsks,
            timestampNs);
    }

    private static BookLevelDto BuildLevelDto(OrderBook book, Side side, Price price)
    {
        var level = book.GetLevel(side, price);

        if (level is null)
        {
            return new BookLevelDto(price.Ticks, 0, 0);
        }

        return new BookLevelDto(
            price.Ticks,
            level.TotalVisibleQuantity.Value,
            level.OrderCount);
    }
}
