using Google.Protobuf;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row 7: bifrost.strategy.v1.BookUpdate ↔ Bifrost.Contracts.Internal.Events.BookDeltaEvent.
///
/// BookUpdate wraps (Instrument, BookView); BookView carries bids/asks +
/// sequence + timestamp_ns. The DTO flattens into InstrumentIdDto +
/// ChangedBids[] + ChangedAsks[] + Sequence + TimestampNs.
/// </summary>
public sealed class BookDeltaTranslationTests
{
    [Fact]
    public void BookUpdate_RoundTrips_ViaDto()
    {
        var original = new StrategyProto.BookUpdate
        {
            Instrument = new MarketProto.Instrument
            {
                InstrumentId = "DE.Hour.2026-04-23T10:00",
                DeliveryArea = "DE",
                DeliveryPeriodStartNs = 1_745_400_000_000_000_000L,
                DeliveryPeriodEndNs = 1_745_403_600_000_000_000L,
                ProductType = MarketProto.ProductType.Hour,
            },
            Book = new MarketProto.BookView
            {
                Sequence = 123_456L,
                TimestampNs = 1_745_400_000_000_000_001L,
            },
        };
        original.Book.Bids.Add(new MarketProto.BookLevel
        {
            PriceTicks = 42_000_000L,
            QuantityTicks = 50_000L,
            OrderCount = 7,
        });
        original.Book.Asks.Add(new MarketProto.BookLevel
        {
            PriceTicks = 42_100_000L,
            QuantityTicks = 30_000L,
            OrderCount = 4,
        });
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original);
        var roundtrip = TranslationFixtures.ToProto(
            dto,
            instrumentId: original.Instrument.InstrumentId,
            productType: original.Instrument.ProductType);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
