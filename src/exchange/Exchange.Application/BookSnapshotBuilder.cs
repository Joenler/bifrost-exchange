using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Domain;

namespace Bifrost.Exchange.Application;

public static class BookSnapshotBuilder
{
    public static BookSnapshotResponse Build(OrderBook book, long sequence, long timestampNs)
    {
        var bids = book.GetLevels(Side.Buy)
            .Select(level => new BookLevelDto(
                level.Price.Ticks,
                level.TotalVisibleQuantity.Value,
                level.OrderCount))
            .ToArray();

        var asks = book.GetLevels(Side.Sell)
            .Select(level => new BookLevelDto(
                level.Price.Ticks,
                level.TotalVisibleQuantity.Value,
                level.OrderCount))
            .ToArray();

        return new BookSnapshotResponse(
            InstrumentIdMapping.ToDto(book.InstrumentId),
            sequence,
            bids,
            asks,
            timestampNs);
    }
}
