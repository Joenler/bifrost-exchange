using System.Text.Json;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.Gateway.Translation;
using Xunit;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Translation;

/// <summary>
/// Production InboundTranslator round-trip tests. Each [Fact] builds a
/// StrategyProto.* using the canonical fixture rows, calls the production
/// translator, and asserts the resulting DTO is record-equal to the seeded DTO
/// from <see cref="TranslationFixturesMirror"/> — which mirrors the existing
/// CONT-07 byte-equivalence fixtures row-for-row. If the production translator
/// drifts from the seed, both this test and the existing
/// <c>tests/Bifrost.Contracts.Translation.Tests</c> suite fail.
/// </summary>
public sealed class InboundTranslatorTests
{
    [Fact]
    public void ToInternalSubmit_MatchesFixtureByteEquivalent()
    {
        var protoIn = TranslationFixturesMirror.OrderSubmitProto();
        var dtoExpected = TranslationFixturesMirror.OrderSubmitDto();

        var dtoActual = InboundTranslator.ToInternalSubmit(protoIn, TranslationFixturesMirror.CanonicalClientId);

        Assert.Equal(dtoExpected, dtoActual);
    }

    [Fact]
    public void ToInternalCancel_MatchesFixtureByteEquivalent()
    {
        var protoIn = TranslationFixturesMirror.OrderCancelProto();
        var dtoExpected = TranslationFixturesMirror.OrderCancelDto();

        var dtoActual = InboundTranslator.ToInternalCancel(protoIn, TranslationFixturesMirror.CanonicalClientId);

        Assert.Equal(dtoExpected, dtoActual);
    }

    [Fact]
    public void ToInternalReplace_MatchesFixtureByteEquivalent()
    {
        var protoIn = TranslationFixturesMirror.OrderReplaceProto();
        var dtoExpected = TranslationFixturesMirror.OrderReplaceDto();

        var dtoActual = InboundTranslator.ToInternalReplace(protoIn, TranslationFixturesMirror.CanonicalClientId);

        Assert.Equal(dtoExpected, dtoActual);
    }

    [Theory]
    [InlineData("quoter")]
    [InlineData("QUOTER")]
    [InlineData("Quoter")]
    [InlineData("dah-auction")]
    [InlineData("DAH-AUCTION")]
    [InlineData("DAH-Auction")]
    public void ToInternalSubmit_ReservedClientId_Throws(string reservedId)
    {
        var p = TranslationFixturesMirror.OrderSubmitProto();
        Assert.Throws<InvalidOperationException>(() => InboundTranslator.ToInternalSubmit(p, reservedId));
    }

    [Theory]
    [InlineData("quoter")]
    [InlineData("dah-auction")]
    public void ToInternalCancel_ReservedClientId_Throws(string reservedId)
    {
        var p = TranslationFixturesMirror.OrderCancelProto();
        Assert.Throws<InvalidOperationException>(() => InboundTranslator.ToInternalCancel(p, reservedId));
    }

    [Theory]
    [InlineData("quoter")]
    [InlineData("dah-auction")]
    public void ToInternalReplace_ReservedClientId_Throws(string reservedId)
    {
        var p = TranslationFixturesMirror.OrderReplaceProto();
        Assert.Throws<InvalidOperationException>(() => InboundTranslator.ToInternalReplace(p, reservedId));
    }

    [Theory]
    [InlineData("quoter", true)]
    [InlineData("QUOTER", true)]
    [InlineData("Quoter", true)]
    [InlineData("qUoTeR", true)]
    [InlineData("dah-auction", true)]
    [InlineData("DAH-Auction", true)]
    [InlineData("DAH-AUCTION", true)]
    [InlineData("alpha", false)]
    [InlineData("team-alpha-1", false)]
    [InlineData("", false)]
    [InlineData("dah_auction", false)]
    [InlineData("quoter-2", false)]
    public void IsReservedClientId_CaseInsensitiveAndExact(string clientId, bool expected)
    {
        Assert.Equal(expected, InboundTranslator.IsReservedClientId(clientId));
    }

    [Fact]
    public void ToInternalSubmit_NullProto_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InboundTranslator.ToInternalSubmit(null!, TranslationFixturesMirror.CanonicalClientId));
    }

    [Fact]
    public void ToInternalSubmit_BlankClientId_Throws()
    {
        var p = TranslationFixturesMirror.OrderSubmitProto();
        Assert.Throws<ArgumentException>(() => InboundTranslator.ToInternalSubmit(p, ""));
        Assert.Throws<ArgumentException>(() => InboundTranslator.ToInternalSubmit(p, "   "));
    }

    [Fact]
    public void ToInternalSubmit_ZeroPriceTicks_MapsToNull()
    {
        var p = TranslationFixturesMirror.OrderSubmitProto();
        p.PriceTicks = 0L;
        var dto = InboundTranslator.ToInternalSubmit(p, TranslationFixturesMirror.CanonicalClientId);
        Assert.Null(dto.PriceTicks);
    }

    [Fact]
    public void ToInternalSubmit_ZeroDisplaySliceTicks_MapsToNull()
    {
        var p = TranslationFixturesMirror.OrderSubmitProto();
        p.DisplaySliceTicks = 0L;
        var dto = InboundTranslator.ToInternalSubmit(p, TranslationFixturesMirror.CanonicalClientId);
        Assert.Null(dto.DisplaySliceSize);
    }

    [Fact]
    public void ToInternalReplace_ZeroNewFields_MapToNull()
    {
        var p = TranslationFixturesMirror.OrderReplaceProto();
        p.NewPriceTicks = 0L;
        p.NewQuantityTicks = 0L;
        var dto = InboundTranslator.ToInternalReplace(p, TranslationFixturesMirror.CanonicalClientId);
        Assert.Null(dto.NewPriceTicks);
        Assert.Null(dto.NewQuantity);
    }

    [Fact]
    public void ToBidMatrixJson_MatchesFixtureShape()
    {
        const string teamName = "alpha";
        var protoIn = TranslationFixturesMirror.BidMatrixSubmitProto();
        var dtoExpected = TranslationFixturesMirror.BidMatrixDtoExpected(teamName);

        var json = InboundTranslator.ToBidMatrixJson(protoIn, teamName);
        var dtoActual = JsonSerializer.Deserialize<BidMatrixDto>(json, TranslationFixturesMirror.JsonOpts);

        Assert.NotNull(dtoActual);
        // Record auto-Equals does not deep-compare arrays — assert field-by-field.
        Assert.Equal(dtoExpected.TeamName, dtoActual.TeamName);
        Assert.Equal(dtoExpected.QuarterId, dtoActual.QuarterId);
        Assert.Equal(dtoExpected.BuySteps, dtoActual.BuySteps);
        Assert.Equal(dtoExpected.SellSteps, dtoActual.SellSteps);
    }

    [Fact]
    public void ToBidMatrixJson_UsesResolvedTeamName_NotProtoTeamName()
    {
        var protoIn = TranslationFixturesMirror.BidMatrixSubmitProto();
        // The proto's matrix.team_name is "alpha-from-client" — should be ignored.
        Assert.Equal("alpha-from-client", protoIn.Matrix.TeamName);

        var json = InboundTranslator.ToBidMatrixJson(protoIn, "alpha-resolved");
        var dto = JsonSerializer.Deserialize<BidMatrixDto>(json, TranslationFixturesMirror.JsonOpts);

        Assert.NotNull(dto);
        Assert.Equal("alpha-resolved", dto.TeamName);
    }
}
