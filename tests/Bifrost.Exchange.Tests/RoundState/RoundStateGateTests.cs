using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Exchange.Tests.Fixtures;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using RoundStateEnum = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.Exchange.Tests.RoundState;

/// <summary>
/// EX-05 integration coverage: the OrderValidator gate-guard drives all 7
/// RoundState values against ValidateSubmit + ValidateReplace + ValidateCancel.
/// Asserts:
///   (a) new-order commands reject with ExchangeClosed for every non-RoundOpen state
///       and the D-11 reason_detail string matches the locked vocabulary;
///   (b) ValidateCancel is NEVER rejected by the RoundState gate, in any state
///       (D-09 / ADR-0004 GW-07 mass-cancel-on-disconnect invariant);
///   (c) Replace behaves as a new-order command (D-09 — atomic cancel+submit);
///   (d) InMemoryRoundStateSource.Set fires OnChange with (previous, current, ts).
/// </summary>
public sealed class RoundStateGateTests
{
    // -------- Harness --------

    private static OrderValidator BuildValidator(IRoundStateSource source)
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
        var clock = new TestClock();
        return new OrderValidator(rules, registry, clock, source);
    }

    private static InstrumentIdDto KnownInstrumentDto()
    {
        // Mirrors TradingCalendar.GenerateInstruments()[0] — the 9999-01-01T00:00Z hour product on DE.
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new InstrumentIdDto("DE", hourStart, hourStart.AddHours(1));
    }

    private static SubmitOrderCommand SampleSubmit() =>
        new(
            ClientId: "team-a",
            InstrumentId: KnownInstrumentDto(),
            Side: "Buy",
            OrderType: "Limit",
            PriceTicks: 100,
            Quantity: 1.0m,
            DisplaySliceSize: null);

    private static CancelOrderCommand SampleCancel() =>
        new(ClientId: "team-a", OrderId: 42, InstrumentId: KnownInstrumentDto());

    private static ReplaceOrderCommand SampleReplace() =>
        new(
            ClientId: "team-a",
            OrderId: 42,
            NewPriceTicks: 110,
            NewQuantity: 1.0m,
            InstrumentId: KnownInstrumentDto());

    // -------- (a) new-order commands — full 7-state theory over ValidateSubmit --------

    [Theory]
    [InlineData(RoundStateEnum.IterationOpen, true,  "round_not_started")]
    [InlineData(RoundStateEnum.AuctionOpen,   true,  "auction_phase")]
    [InlineData(RoundStateEnum.AuctionClosed, true,  "auction_phase")]
    [InlineData(RoundStateEnum.RoundOpen,     false, null)]
    [InlineData(RoundStateEnum.Gate,          true,  "gate_reached")]
    [InlineData(RoundStateEnum.Settled,       true,  "round_settled")]
    [InlineData(RoundStateEnum.Aborted,       true,  "aborted")]
    public void ValidateSubmit_GatesByRoundState(RoundStateEnum rs, bool expectReject, string? expectedDetail)
    {
        var source = new InMemoryRoundStateSource(new TestClock(), rs);
        var validator = BuildValidator(source);

        var result = validator.ValidateSubmit(SampleSubmit());

        if (expectReject)
        {
            Assert.False(result.IsValid);
            Assert.Equal(RejectionCode.ExchangeClosed, result.Code);
            Assert.Equal(expectedDetail, result.RejectionReason);
        }
        else
        {
            // RoundOpen — the gate-guard must be transparent. Downstream validations may
            // still reject on bad input, but the reject MUST NOT be ExchangeClosed.
            if (!result.IsValid)
            {
                Assert.NotEqual(RejectionCode.ExchangeClosed, result.Code);
            }
            else
            {
                Assert.True(result.IsValid);
            }
        }
    }

    // -------- (c) Replace behaves as new-order — same 7-state theory --------

    [Theory]
    [InlineData(RoundStateEnum.IterationOpen, true,  "round_not_started")]
    [InlineData(RoundStateEnum.AuctionOpen,   true,  "auction_phase")]
    [InlineData(RoundStateEnum.AuctionClosed, true,  "auction_phase")]
    [InlineData(RoundStateEnum.RoundOpen,     false, null)]
    [InlineData(RoundStateEnum.Gate,          true,  "gate_reached")]
    [InlineData(RoundStateEnum.Settled,       true,  "round_settled")]
    [InlineData(RoundStateEnum.Aborted,       true,  "aborted")]
    public void ValidateReplace_GatesByRoundState(RoundStateEnum rs, bool expectReject, string? expectedDetail)
    {
        var source = new InMemoryRoundStateSource(new TestClock(), rs);
        var validator = BuildValidator(source);

        var result = validator.ValidateReplace(SampleReplace());

        if (expectReject)
        {
            Assert.False(result.IsValid);
            Assert.Equal(RejectionCode.ExchangeClosed, result.Code);
            Assert.Equal(expectedDetail, result.RejectionReason);
        }
        else
        {
            if (!result.IsValid)
            {
                Assert.NotEqual(RejectionCode.ExchangeClosed, result.Code);
            }
            else
            {
                Assert.True(result.IsValid);
            }
        }
    }

    // -------- (b) Cancel is never rejected by the gate in any state (D-09) --------

    [Fact]
    public void ValidateCancel_AlwaysValid_AcrossAllRoundStates()
    {
        foreach (var rs in Enum.GetValues<RoundStateEnum>())
        {
            var source = new InMemoryRoundStateSource(new TestClock(), rs);
            var validator = BuildValidator(source);

            var result = validator.ValidateCancel(SampleCancel());

            Assert.True(result.IsValid, $"Cancel was rejected in state {rs} — D-09 (ADR-0004 GW-07) requires it to always pass the gate guard.");
            Assert.NotEqual(RejectionCode.ExchangeClosed, result.Code);
        }
    }

    // -------- (d) InMemoryRoundStateSource.Set fires OnChange with correct payload --------

    [Fact]
    public void InMemoryRoundStateSource_Set_FiresOnChangeWithPreviousAndCurrentAndTimestamp()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var clock = new TestClock(fakeTime);
        var source = new InMemoryRoundStateSource(clock, RoundStateEnum.IterationOpen);

        RoundStateChangedEventArgs? captured = null;
        source.OnChange += (_, e) => captured = e;

        source.Set(RoundStateEnum.AuctionOpen);

        Assert.NotNull(captured);
        Assert.Equal(RoundStateEnum.IterationOpen, captured!.Previous);
        Assert.Equal(RoundStateEnum.AuctionOpen, captured.Current);

        // Expect timestamp = Unix ms * 1_000_000 from the fake clock.
        var expectedMs = fakeTime.GetUtcNow().ToUnixTimeMilliseconds();
        Assert.Equal(expectedMs * 1_000_000, captured.TimestampNs);

        // Source now reports the new state.
        Assert.Equal(RoundStateEnum.AuctionOpen, source.Current);
    }

    [Fact]
    public void InMemoryRoundStateSource_Set_NoOpDoesNotFireOnChange()
    {
        var clock = new TestClock();
        var source = new InMemoryRoundStateSource(clock, RoundStateEnum.RoundOpen);

        var fired = 0;
        source.OnChange += (_, _) => fired++;

        source.Set(RoundStateEnum.RoundOpen);

        Assert.Equal(0, fired);
    }

    // -------- RejectionCode fidelity — a single pinpoint fact isolating the code value --------

    [Fact]
    public void ValidateSubmit_InGateState_UsesExchangeClosedRejectCodeSpecifically()
    {
        var source = new InMemoryRoundStateSource(new TestClock(), RoundStateEnum.Gate);
        var validator = BuildValidator(source);

        var result = validator.ValidateSubmit(SampleSubmit());

        Assert.False(result.IsValid);
        Assert.Equal(RejectionCode.ExchangeClosed, result.Code);
        Assert.Equal("gate_reached", result.RejectionReason);
    }
}
