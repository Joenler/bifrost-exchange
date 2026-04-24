using Google.Protobuf;
using Xunit;
using EventsProto = Bifrost.Contracts.Events;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// bifrost.events.v1.ForecastRevision ↔ Bifrost.Contracts.Internal.Events.ForecastRevisionEvent.
///
/// ForecastRevision carries (new_forecast_price_ticks, reason). DTO adds
/// TimestampNs, which on the proto side lives on the enclosing Event
/// envelope — passed through ToInternal and dropped on ToProto.
/// </summary>
public sealed class ForecastRevisionTranslationTests
{
    [Fact]
    public void ForecastRevision_RoundTrips_ViaDto()
    {
        var original = new EventsProto.ForecastRevision
        {
            NewForecastPriceTicks = 48_750_000L,
            Reason = "mc_revise",
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original, timestampNs: 1_745_400_000_000_000_151L);

        var roundtrip = TranslationFixtures.ToProto(dto);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
