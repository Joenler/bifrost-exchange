using Google.Protobuf;
using Xunit;
using AuctionProto = Bifrost.Contracts.Auction;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row C: bifrost.auction.v1.ClearingResult &lt;-&gt; Bifrost.Contracts.Internal.Auction.ClearingResultDto.
/// Two facts cover the TeamName null &lt;-&gt; "" asymmetry (proto3 string has no
/// "absent" representation; DTO uses nullable to distinguish the public-summary
/// row from a per-team row).
/// </summary>
public sealed class AuctionClearingResultTranslationTests
{
    [Fact]
    public void ClearingResult_SummaryForm_MapsNullTeamNameToEmptyString()
    {
        // Public summary row on bifrost.auction.cleared.<qh>: team_name == ""
        // on the wire, TeamName == null on the DTO, AwardedQuantityTicks == 0.
        var original = new AuctionProto.ClearingResult
        {
            QuarterId = "DE.Quarter.9999-01-01T00:15",
            ClearingPriceTicks = 85_000L,
            AwardedQuantityTicks = 0L,
            TeamName = string.Empty,
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original);
        Assert.Null(dto.TeamName);

        var roundtrip = TranslationFixtures.ToProto(dto);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }

    [Fact]
    public void ClearingResult_PerTeamForm_RoundTrips_ViaDto()
    {
        // Per-team row: TeamName = "alpha", AwardedQuantityTicks != 0
        // (positive = net buy; negative = net sell).
        var original = new AuctionProto.ClearingResult
        {
            QuarterId = "DE.Quarter.9999-01-01T00:15",
            ClearingPriceTicks = 85_000L,
            AwardedQuantityTicks = 50_000L,
            TeamName = "alpha",
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original);
        Assert.Equal("alpha", dto.TeamName);

        var roundtrip = TranslationFixtures.ToProto(dto);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
