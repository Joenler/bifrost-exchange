using Google.Protobuf;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row 8: bifrost.strategy.v1.Trade ↔ Bifrost.Contracts.Internal.Events.PublicTradeEvent.
///
/// DTO carries TickSize (exchange-config; not on the wire per message) and
/// TimestampNs (envelope-level on the proto side); both ride into ToInternal
/// as extra parameters.
/// </summary>
public sealed class PublicTradeTranslationTests
{
    [Fact]
    public void Trade_RoundTrips_ViaDto()
    {
        var original = new StrategyProto.Trade
        {
            Instrument = new MarketProto.Instrument
            {
                InstrumentId = "DE.Hour.2026-04-23T10:00",
                DeliveryArea = "DE",
                DeliveryPeriodStartNs = 1_745_400_000_000_000_000L,
                DeliveryPeriodEndNs = 1_745_403_600_000_000_000L,
                ProductType = MarketProto.ProductType.Hour,
            },
            TradeId = 77_777L,
            PriceTicks = 42_500_000L,
            QuantityTicks = 15_000L,
            AggressorSide = MarketProto.Side.Sell,
            Sequence = 3L,
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(
            original,
            tickSize: 100L,
            timestampNs: 1_745_400_000_000_000_103L);

        var roundtrip = TranslationFixtures.ToProto(
            dto,
            instrumentId: original.Instrument.InstrumentId,
            productType: original.Instrument.ProductType);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
