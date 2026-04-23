using Google.Protobuf;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row 2: bifrost.strategy.v1.OrderCancel ↔ Bifrost.Contracts.Internal.Commands.CancelOrderCommand.
/// </summary>
public sealed class OrderCancelTranslationTests
{
    [Fact]
    public void OrderCancel_RoundTrips_ViaDto()
    {
        var original = new StrategyProto.OrderCancel
        {
            ClientId = "client-001",
            OrderId = 999_111L,
            Instrument = new MarketProto.Instrument
            {
                InstrumentId = "DE.Hour.2026-04-23T10:00",
                DeliveryArea = "DE",
                DeliveryPeriodStartNs = 1_745_400_000_000_000_000L,
                DeliveryPeriodEndNs = 1_745_403_600_000_000_000L,
                ProductType = MarketProto.ProductType.Hour,
            },
        };
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
