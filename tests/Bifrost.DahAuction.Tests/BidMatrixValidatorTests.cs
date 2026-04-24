using Bifrost.Contracts.Internal.Auction;
using Bifrost.DahAuction.Validation;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Domain;
using Xunit;

namespace Bifrost.DahAuction.Tests;

/// <summary>
/// Validator unit tests covering the six structural rejection codes
/// (Structural is raised at the HTTP JSON binding layer and is not
/// exercised by this class) and three positive cases (negative price,
/// empty buy-side, empty sell-side).
/// </summary>
public sealed class BidMatrixValidatorTests
{
    private const int MaxStepsPerSide = 20;

    private static BidMatrixValidator NewValidator()
    {
        // Build a registry whose key-set matches the canonical 1 hour + 4 quarter-hour
        // TradingCalendar layout. The validator only consumes
        // registry.GetQuarterInstruments(), so any MatchingEngine stub that registers
        // the 5 instrument keys is sufficient — mirrors the recipe used by
        // InstrumentRegistryTests in the Bifrost.Exchange.Tests project.
        var instruments = TradingCalendar.GenerateInstruments();
        var engines = instruments
            .Select(id => new MatchingEngine(new OrderBook(id), new MonotonicSequenceGenerator()))
            .ToList();
        var registry = new InstrumentRegistry(engines);
        return new BidMatrixValidator(registry, MaxStepsPerSide);
    }

    private static string FirstQuarterId() =>
        TradingCalendar.GenerateInstruments()
            .First(i => (i.DeliveryPeriod.End - i.DeliveryPeriod.Start) == TimeSpan.FromMinutes(15))
            .ToString();

    private static string HourInstrumentId() =>
        TradingCalendar.GenerateInstruments()
            .First(i => (i.DeliveryPeriod.End - i.DeliveryPeriod.Start) == TimeSpan.FromMinutes(60))
            .ToString();

    private static BidMatrixDto ValidMatrix(string? teamName = null, string? quarterId = null) => new(
        TeamName: teamName ?? "alpha",
        QuarterId: quarterId ?? FirstQuarterId(),
        BuySteps: new BidStepDto[] { new(100_000L, 30_000L), new(80_000L, 20_000L) },
        SellSteps: new BidStepDto[] { new(110_000L, 30_000L), new(130_000L, 20_000L) });

    [Fact]
    public void ValidMatrix_AcceptedWithOkResult()
    {
        var v = NewValidator();
        var r = v.Validate(ValidMatrix());
        Assert.False(r.IsError);
        Assert.NotNull(r.Value);
    }

    [Fact]
    public void EmptyTeamName_RejectedWithUnknownTeam()
    {
        var v = NewValidator();
        var r = v.Validate(ValidMatrix(teamName: ""));
        Assert.True(r.IsError);
        Assert.Equal("UnknownTeam", r.Error!.Code);
        Assert.Equal("empty_team_name", r.Error!.Detail);
    }

    [Fact]
    public void WhitespaceTeamName_RejectedWithUnknownTeam()
    {
        var v = NewValidator();
        var r = v.Validate(ValidMatrix(teamName: "   "));
        Assert.True(r.IsError);
        Assert.Equal("UnknownTeam", r.Error!.Code);
        Assert.Equal("whitespace_team_name", r.Error!.Detail);
    }

    [Fact]
    public void UnregisteredQuarterId_RejectedWithUnknownQuarter()
    {
        var v = NewValidator();
        var r = v.Validate(ValidMatrix(quarterId: "DE-NOT-A-QH"));
        Assert.True(r.IsError);
        Assert.Equal("UnknownQuarter", r.Error!.Code);
    }

    [Fact]
    public void HourInstrument_Rejected()
    {
        // The hour instrument is in the registry but not in the QH filter;
        // validator must reject it as UnknownQuarter (Hour has no DAH bid).
        var v = NewValidator();
        var r = v.Validate(ValidMatrix(quarterId: HourInstrumentId()));
        Assert.True(r.IsError);
        Assert.Equal("UnknownQuarter", r.Error!.Code);
    }

    [Fact]
    public void TooManyBuySteps_RejectedWithTooManySteps()
    {
        var v = NewValidator();
        // 21 strictly-descending steps > MaxStepsPerSide=20
        var buys = Enumerable.Range(0, 21)
            .Select(i => new BidStepDto(1_000_000L - i * 1_000L, 10_000L))
            .ToArray();
        var matrix = ValidMatrix() with { BuySteps = buys };
        var r = v.Validate(matrix);
        Assert.True(r.IsError);
        Assert.Equal("TooManySteps", r.Error!.Code);
    }

    [Fact]
    public void NonStrictDescendingBuy_RejectedWithNonMonotonic()
    {
        var v = NewValidator();
        var buys = new BidStepDto[] { new(100_000L, 10_000L), new(100_000L, 10_000L) }; // duplicate price
        var matrix = ValidMatrix() with { BuySteps = buys };
        var r = v.Validate(matrix);
        Assert.True(r.IsError);
        Assert.Equal("NonMonotonic", r.Error!.Code);
    }

    [Fact]
    public void NonStrictAscendingSell_RejectedWithNonMonotonic()
    {
        var v = NewValidator();
        var sells = new BidStepDto[] { new(80_000L, 10_000L), new(100_000L, 10_000L), new(90_000L, 10_000L) };
        var matrix = ValidMatrix() with { SellSteps = sells };
        var r = v.Validate(matrix);
        Assert.True(r.IsError);
        Assert.Equal("NonMonotonic", r.Error!.Code);
    }

    [Fact]
    public void ZeroQuantity_RejectedWithNonPositiveQuantity()
    {
        var v = NewValidator();
        var buys = new BidStepDto[] { new(100_000L, 0L) };
        var matrix = ValidMatrix() with { BuySteps = buys };
        var r = v.Validate(matrix);
        Assert.True(r.IsError);
        Assert.Equal("NonPositiveQuantity", r.Error!.Code);
    }

    [Fact]
    public void NegativeQuantity_RejectedWithNonPositiveQuantity()
    {
        var v = NewValidator();
        var sells = new BidStepDto[] { new(100_000L, -5_000L) };
        var matrix = ValidMatrix() with { SellSteps = sells };
        var r = v.Validate(matrix);
        Assert.True(r.IsError);
        Assert.Equal("NonPositiveQuantity", r.Error!.Code);
    }

    [Fact]
    public void NegativePrice_Accepted_AndNotMistakenForNonPositiveQuantity()
    {
        // Nordic / CE DAH convention: negative prices are valid bids.
        var v = NewValidator();
        var buys = new BidStepDto[] { new(-500_000L, 10_000L) };  // buy price below zero
        var sells = new BidStepDto[] { new(-300_000L, 10_000L), new(100_000L, 10_000L) };  // sells ascending
        var matrix = ValidMatrix() with { BuySteps = buys, SellSteps = sells };
        var r = v.Validate(matrix);
        Assert.False(r.IsError);
    }

    [Fact]
    public void EmptyBuySide_Accepted_TeamIsSellOnly()
    {
        var v = NewValidator();
        var matrix = ValidMatrix() with { BuySteps = Array.Empty<BidStepDto>() };
        var r = v.Validate(matrix);
        Assert.False(r.IsError);
    }

    [Fact]
    public void EmptySellSide_Accepted_TeamIsBuyOnly()
    {
        var v = NewValidator();
        var matrix = ValidMatrix() with { SellSteps = Array.Empty<BidStepDto>() };
        var r = v.Validate(matrix);
        Assert.False(r.IsError);
    }
}
