using Google.Protobuf;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row 3: bifrost.strategy.v1.OrderReplace ↔ Bifrost.Contracts.Internal.Commands.ReplaceOrderCommand.
/// </summary>
public sealed class OrderReplaceTranslationTests
{
    [Fact]
    public void OrderReplace_RoundTrips_ViaDto()
    {
        var original = new StrategyProto.OrderReplace
        {
            ClientId = "client-001",
            OrderId = 999_111L,
            NewPriceTicks = 43_000_000L,
            NewQuantityTicks = 60_000L,
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
