using Google.Protobuf;
using Xunit;
using EventsProto = Bifrost.Contracts.Events;
using MarketProto = Bifrost.Contracts.Market;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// bifrost.market.v1.ImbalancePrint ↔ Bifrost.Contracts.Internal.Events.ImbalancePrintEvent.
///
/// ImbalancePrint is the most self-contained of the v1.1.0 additions — proto
/// carries every field the DTO does, including timestamp_ns inline. Only
/// Regime enum ↔ string and Instrument ↔ InstrumentIdDto require
/// translation; the rest is 1:1.
/// </summary>
public sealed class ImbalancePrintTranslationTests
{
    [Fact]
    public void ImbalancePrint_RoundTrips_ViaDto_PreservingAllFields()
    {
        var original = new MarketProto.ImbalancePrint
        {
            RoundNumber = 42,
            Instrument = new MarketProto.Instrument
            {
                InstrumentId = "DE.Quarter.2026-04-23T10:00.Q2",
                DeliveryArea = "DE",
                DeliveryPeriodStartNs = 1_745_400_000_000_000_000L,
                DeliveryPeriodEndNs = 1_745_400_900_000_000_000L,
                ProductType = MarketProto.ProductType.Quarter,
            },
            QuarterIndex = 2,
            PImbTicks = 5_200_000L,
            ATotalTicks = -30_000_000L,
            APhysicalTicks = -30_000_000L,
            Regime = EventsProto.Regime.Volatile,
            TimestampNs = 1_745_400_000_000_000_109L,
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
