using Google.Protobuf;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row 4: bifrost.strategy.v1.OrderAck ↔ Bifrost.Contracts.Internal.Events.OrderAcceptedEvent.
///
/// OrderAck proto carries only (client_order_id, order_id, instrument). The
/// DTO OrderAcceptedEvent carries the fuller "order accepted" fingerprint
/// (client_id, side, order_type, price_ticks, quantity, display_slice, ts).
/// For the CONT-07 test we carry the DTO-only fields into ToInternal as
/// extra parameters ("state Phase 07 will reconstruct from the originating
/// SubmitOrderCommand + envelope"); the proto-only client_order_id rides
/// through ToProto as an extra parameter. The byte-equivalence assertion is
/// still on the 3 proto-present fields on the wire.
/// </summary>
public sealed class OrderAcceptedTranslationTests
{
    [Fact]
    public void OrderAck_RoundTrips_ViaDto()
    {
        var original = new StrategyProto.OrderAck
        {
            ClientOrderId = "co-12345",
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

        // Act — DTO-only fields threaded through ToInternal (Phase 07 sources
        // them from the originating SubmitOrderCommand + MarketEvent envelope).
        var dto = TranslationFixtures.ToInternal(
            original,
            clientId: "client-001",
            side: MarketProto.Side.Buy,
            orderType: MarketProto.OrderType.Limit,
            priceTicks: 42_000_000L,
            quantity: 5.0m,
            displaySliceSize: 1.0m,
            timestampNs: 1_745_400_000_000_000_106L);

        var roundtrip = TranslationFixtures.ToProto(
            dto,
            clientOrderId: original.ClientOrderId,
            instrumentId: original.Instrument.InstrumentId,
            productType: original.Instrument.ProductType);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
