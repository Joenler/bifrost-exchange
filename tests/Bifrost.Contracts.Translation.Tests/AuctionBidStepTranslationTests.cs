using Google.Protobuf;
using Xunit;
using AuctionProto = Bifrost.Contracts.Auction;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row A: bifrost.auction.v1.BidStep &lt;-&gt; Bifrost.Contracts.Internal.Auction.BidStepDto.
/// Both fields are int64 on the wire and long on the DTO — no QuantityScale
/// conversion because auction protos carry int64 quantity_ticks directly.
/// </summary>
public sealed class AuctionBidStepTranslationTests
{
    [Fact]
    public void BidStep_RoundTrips_ViaDto()
    {
        var original = new AuctionProto.BidStep
        {
            PriceTicks = 4_250_000L,
            QuantityTicks = 50_000L,
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original);
        var roundtrip = TranslationFixtures.ToProto(dto);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }

    [Fact]
    public void BidStep_NegativePrice_RoundTrips_ViaDto()
    {
        // Nordic / CE DAH convention: renewable-surplus hours can clear negative.
        var original = new AuctionProto.BidStep
        {
            PriceTicks = -500_000L,
            QuantityTicks = 30_000L,
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original);
        var roundtrip = TranslationFixtures.ToProto(dto);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
