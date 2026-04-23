using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Domain;
using Bifrost.Time;
using Xunit;

namespace Bifrost.Exchange.Tests;

/// <summary>
/// EX-02 typed-reject coverage for the donated Arena OrderValidator.
/// Exercises distinct RejectionCode values (UnknownInstrument, InvalidSide,
/// InvalidOrderType, PriceNotAlignedToTickSize, QuantityBelowMinimum) against
/// crafted bad SubmitOrderCommands to lock the reject vocabulary in.
///
/// RoundState/ExchangeClosed is deliberately NOT covered here — the
/// IRoundStateSource parameter + gate guard land in a later plan. This
/// suite is the "wave 0" baseline that guards the Plan 02 fork semantics.
/// </summary>
public sealed class OrderValidatorTests
{
    private static OrderValidator BuildValidator()
    {
        var engines = TradingCalendar.GenerateInstruments()
            .Select(id => new MatchingEngine(new OrderBook(id), new MonotonicSequenceGenerator()))
            .ToList();
        var registry = new InstrumentRegistry(engines);
        var rules = new ExchangeRulesConfig(
            TickSize: 10,
            MinQuantity: 0.1m,
            QuantityStep: 0.1m,
            MakerFeeRate: 0.01m,
            TakerFeeRate: 0.02m,
            PriceScale: 10);
        var clock = new SystemClock();
        return new OrderValidator(rules, registry, clock);
    }

    private static InstrumentIdDto KnownInstrumentDto()
    {
        // Mirrors TradingCalendar.GenerateInstruments()[0] — the 9999-01-01T00:00Z hour product on DE.
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new InstrumentIdDto("DE", hourStart, hourStart.AddHours(1));
    }

    private static SubmitOrderCommand BaselineSubmit() =>
        new(
            ClientId: "team-a",
            InstrumentId: KnownInstrumentDto(),
            Side: "Buy",
            OrderType: "Limit",
            PriceTicks: 100,    // aligned to TickSize=10
            Quantity: 1.0m,     // >= MinQuantity 0.1 and aligned to QuantityStep 0.1
            DisplaySliceSize: null);

    [Fact]
    public void ValidateSubmit_BaselineCommand_IsValid()
    {
        var validator = BuildValidator();
        var result = validator.ValidateSubmit(BaselineSubmit());
        Assert.True(result.IsValid);
        Assert.Null(result.Code);
    }

    [Fact]
    public void ValidateSubmit_UnparseableSide_RejectsWithInvalidSide()
    {
        var validator = BuildValidator();
        var cmd = BaselineSubmit() with { Side = "Sideways" };

        var result = validator.ValidateSubmit(cmd);

        Assert.False(result.IsValid);
        Assert.Equal(RejectionCode.InvalidSide, result.Code);
        Assert.Contains("Invalid side", result.RejectionReason);
    }

    [Fact]
    public void ValidateSubmit_UnparseableOrderType_RejectsWithInvalidOrderType()
    {
        var validator = BuildValidator();
        var cmd = BaselineSubmit() with { OrderType = "Teleport" };

        var result = validator.ValidateSubmit(cmd);

        Assert.False(result.IsValid);
        Assert.Equal(RejectionCode.InvalidOrderType, result.Code);
    }

    [Fact]
    public void ValidateSubmit_UnknownInstrument_RejectsWithUnknownInstrument()
    {
        var validator = BuildValidator();
        var unknown = new InstrumentIdDto(
            "XX",
            new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(9999, 1, 1, 1, 0, 0, TimeSpan.Zero));
        var cmd = BaselineSubmit() with { InstrumentId = unknown };

        var result = validator.ValidateSubmit(cmd);

        Assert.False(result.IsValid);
        Assert.Equal(RejectionCode.UnknownInstrument, result.Code);
    }

    [Fact]
    public void ValidateSubmit_PriceNotAlignedToTickSize_RejectsWithPriceNotAligned()
    {
        var validator = BuildValidator();
        var cmd = BaselineSubmit() with { PriceTicks = 105 }; // TickSize=10, 105 % 10 != 0

        var result = validator.ValidateSubmit(cmd);

        Assert.False(result.IsValid);
        Assert.Equal(RejectionCode.PriceNotAlignedToTickSize, result.Code);
    }

    [Fact]
    public void ValidateSubmit_QuantityBelowMinimum_RejectsWithQuantityBelowMinimum()
    {
        var validator = BuildValidator();
        var cmd = BaselineSubmit() with { Quantity = 0.05m }; // < MinQuantity 0.1

        var result = validator.ValidateSubmit(cmd);

        Assert.False(result.IsValid);
        Assert.Equal(RejectionCode.QuantityBelowMinimum, result.Code);
    }

    [Fact]
    public void ValidateSubmit_QuantityNotAlignedToStep_RejectsWithQuantityNotAligned()
    {
        var validator = BuildValidator();
        // MinQuantity=0.1, QuantityStep=0.1. 0.25 is >= min but 0.25 % 0.1 != 0.
        var cmd = BaselineSubmit() with { Quantity = 0.25m };

        var result = validator.ValidateSubmit(cmd);

        Assert.False(result.IsValid);
        Assert.Equal(RejectionCode.QuantityNotAlignedToStep, result.Code);
    }

    [Fact]
    public void ValidateSubmit_LimitWithoutPrice_IsValidFromValidatorPerspective()
    {
        // OrderValidator does NOT enforce "Limit requires price" — ExchangeService does.
        // Lock this separation of concerns so Phase 06's gate-guard addition doesn't accidentally
        // conflate validator-level validation with service-level orchestration.
        var validator = BuildValidator();
        var cmd = BaselineSubmit() with { PriceTicks = null };

        var result = validator.ValidateSubmit(cmd);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateCancel_UnknownInstrument_RejectsWithUnknownInstrument()
    {
        var validator = BuildValidator();
        var unknown = new InstrumentIdDto(
            "XX",
            new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(9999, 1, 1, 1, 0, 0, TimeSpan.Zero));
        var cmd = new CancelOrderCommand("team-a", OrderId: 42, InstrumentId: unknown);

        var result = validator.ValidateCancel(cmd);

        Assert.False(result.IsValid);
        Assert.Equal(RejectionCode.UnknownInstrument, result.Code);
    }

    [Fact]
    public void ValidateReplace_PriceNotAlignedToTickSize_RejectsWithPriceNotAligned()
    {
        var validator = BuildValidator();
        var cmd = new ReplaceOrderCommand(
            ClientId: "team-a",
            OrderId: 42,
            NewPriceTicks: 105,   // TickSize=10, not aligned
            NewQuantity: 1.0m,
            InstrumentId: KnownInstrumentDto());

        var result = validator.ValidateReplace(cmd);

        Assert.False(result.IsValid);
        Assert.Equal(RejectionCode.PriceNotAlignedToTickSize, result.Code);
    }

    [Fact]
    public void TradingCalendar_GenerateInstruments_ReturnsExactlyFiveDEInstruments()
    {
        // Lock the BIFROST static-registry invariant so an accidental Arena-style
        // rolling-window regression is caught immediately (Plan 06 orchestrator will
        // replace this when real round timelines exist).
        var instruments = TradingCalendar.GenerateInstruments();

        Assert.Equal(5, instruments.Count);
        Assert.All(instruments, inst => Assert.Equal("DE", inst.DeliveryArea.Value));
        // All delivery periods fall within the synthetic far-future hour
        // 9999-01-01T00:00Z..9999-01-01T01:00Z (one hour product + four 15-min quarters).
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var hourEnd = hourStart.AddHours(1);
        Assert.All(instruments, inst =>
        {
            Assert.True(inst.DeliveryPeriod.Start >= hourStart);
            Assert.True(inst.DeliveryPeriod.End <= hourEnd);
        });
    }
}
