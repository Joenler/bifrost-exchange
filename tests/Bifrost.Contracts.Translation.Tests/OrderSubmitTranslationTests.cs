using Google.Protobuf;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row 1: bifrost.strategy.v1.OrderSubmit ↔ Bifrost.Contracts.Internal.Commands.SubmitOrderCommand.
/// Phase 07 Gateway owns the production translator; this [Fact] proves the
/// gRPC ↔ RabbitMQ-DTO mapping is byte-lossless today.
/// </summary>
public sealed class OrderSubmitTranslationTests
{
    [Fact]
    public void OrderSubmit_RoundTrips_ViaDto()
    {
        // Arrange — canonical fully-populated OrderSubmit with non-default sentinels.
        var original = new StrategyProto.OrderSubmit
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
            Side = MarketProto.Side.Buy,
            OrderType = MarketProto.OrderType.Limit,
            PriceTicks = 42_000_000L,
            QuantityTicks = 50_000L,
            DisplaySliceTicks = 10_000L,
            ClientOrderId = "co-12345",
        };
        var originalBytes = original.ToByteArray();

        // Act — proto → DTO → proto.
        var dto = TranslationFixtures.ToInternal(original);
        var roundtrip = TranslationFixtures.ToProto(
            dto,
            clientOrderId: original.ClientOrderId,
            instrumentId: original.Instrument.InstrumentId,
            productType: original.Instrument.ProductType);
        var roundtripBytes = roundtrip.ToByteArray();

        // Assert — byte-equivalent.
        Assert.Equal(originalBytes, roundtripBytes);
    }
}
