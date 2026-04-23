using Google.Protobuf;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row 6: bifrost.strategy.v1.Fill ↔ Bifrost.Contracts.Internal.Events.OrderExecutedEvent.
///
/// DTO carries TimestampNs (envelope-level on the proto side); we thread it
/// through ToInternal so the round-trip can reproduce byte-identical proto.
/// </summary>
public sealed class OrderExecutedTranslationTests
{
    [Fact]
    public void Fill_RoundTrips_ViaDto()
    {
        var original = new StrategyProto.Fill
        {
            ClientId = "client-001",
            Instrument = new MarketProto.Instrument
            {
                InstrumentId = "DE.Hour.2026-04-23T10:00",
                DeliveryArea = "DE",
                DeliveryPeriodStartNs = 1_745_400_000_000_000_000L,
                DeliveryPeriodEndNs = 1_745_403_600_000_000_000L,
                ProductType = MarketProto.ProductType.Hour,
            },
            OrderId = 999_111L,
            TradeId = 77_777L,
            PriceTicks = 42_500_000L,
            FilledQuantityTicks = 15_000L,
            RemainingQuantityTicks = 35_000L,
            Side = MarketProto.Side.Buy,
            IsAggressor = true,
            FeeTicks = 250L,
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original, timestampNs: 1_745_400_000_000_000_108L);

        var roundtrip = TranslationFixtures.ToProto(
            dto,
            instrumentId: original.Instrument.InstrumentId,
            productType: original.Instrument.ProductType);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
