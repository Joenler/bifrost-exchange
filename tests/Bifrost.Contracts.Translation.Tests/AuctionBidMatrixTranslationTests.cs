using Google.Protobuf;
using Xunit;
using AuctionProto = Bifrost.Contracts.Auction;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row B: bifrost.auction.v1.BidMatrix &lt;-&gt; Bifrost.Contracts.Internal.Auction.BidMatrixDto.
/// Every proto field has a DTO counterpart; no extras. Test populates two
/// descending buy steps and two ascending sell steps for non-trivial coverage.
/// </summary>
public sealed class AuctionBidMatrixTranslationTests
{
    [Fact]
    public void BidMatrix_RoundTrips_ViaDto()
    {
        var original = new AuctionProto.BidMatrix
        {
            TeamName = "alpha",
            QuarterId = "DE.Quarter.9999-01-01T00:15",
        };
        original.BuySteps.Add(new AuctionProto.BidStep { PriceTicks = 100_000L, QuantityTicks = 30_000L });
        original.BuySteps.Add(new AuctionProto.BidStep { PriceTicks = 80_000L,  QuantityTicks = 20_000L });
        original.SellSteps.Add(new AuctionProto.BidStep { PriceTicks = 70_000L, QuantityTicks = 40_000L });
        original.SellSteps.Add(new AuctionProto.BidStep { PriceTicks = 95_000L, QuantityTicks = 25_000L });
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original);
        var roundtrip = TranslationFixtures.ToProto(dto);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
