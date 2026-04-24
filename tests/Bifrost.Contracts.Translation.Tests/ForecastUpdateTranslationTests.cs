using Google.Protobuf;
using Xunit;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// bifrost.strategy.v1.ForecastUpdate ↔ Bifrost.Contracts.Internal.Events.ForecastUpdateEvent.
///
/// ForecastUpdate carries (forecast_price_ticks, horizon_ns). DTO adds
/// TimestampNs, which on the proto side lives on the enclosing MarketEvent
/// envelope — passed through ToInternal and dropped on ToProto, so bit
/// equivalence of the bare ForecastUpdate holds.
/// </summary>
public sealed class ForecastUpdateTranslationTests
{
    [Fact]
    public void ForecastUpdate_RoundTrips_ViaDto()
    {
        var original = new StrategyProto.ForecastUpdate
        {
            ForecastPriceTicks = 52_000_000L,
            HorizonNs = 1_745_400_600_000_000_000L,
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original, timestampNs: 1_745_400_000_000_000_150L);

        var roundtrip = TranslationFixtures.ToProto(dto);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
