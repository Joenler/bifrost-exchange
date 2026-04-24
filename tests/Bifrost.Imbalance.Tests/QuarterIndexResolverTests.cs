using Bifrost.Contracts.Internal;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Domain;
using Xunit;

namespace Bifrost.Imbalance.Tests;

public class QuarterIndexResolverTests
{
    private readonly QuarterIndexResolver _resolver = new();

    [Fact]
    public void Resolve_MapsAllFiveTradingCalendarInstruments()
    {
        // TradingCalendar returns the canonical 5-instrument set: [hour, Q1, Q2, Q3, Q4].
        // The resolver must map the four quarter entries to 0..3 and the hour to null.
        var instruments = TradingCalendar.GenerateInstruments();
        Assert.Equal(5, instruments.Count);

        Assert.Null(_resolver.Resolve(ToDto(instruments[0])));   // hour
        Assert.Equal(0, _resolver.Resolve(ToDto(instruments[1]))); // Q1
        Assert.Equal(1, _resolver.Resolve(ToDto(instruments[2]))); // Q2
        Assert.Equal(2, _resolver.Resolve(ToDto(instruments[3]))); // Q3
        Assert.Equal(3, _resolver.Resolve(ToDto(instruments[4]))); // Q4
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 1)]
    [InlineData(30, 2)]
    [InlineData(45, 3)]
    public void Resolve_ByDto_QuarterStartMinuteDecidesIndex(int startMinute, int expectedIndex)
    {
        var start = new DateTimeOffset(9999, 1, 1, 0, startMinute, 0, TimeSpan.Zero);
        var end = start.AddMinutes(15);
        var dto = new InstrumentIdDto("DE", start, end);

        Assert.Equal(expectedIndex, _resolver.Resolve(dto));
    }

    [Fact]
    public void Resolve_ByDto_HourInstrumentReturnsNull()
    {
        var start = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        var dto = new InstrumentIdDto("DE", start, end);

        Assert.Null(_resolver.Resolve(dto));
    }

    [Fact]
    public void Resolve_ByDto_OffGridMinuteReturnsNull()
    {
        // A 15-minute window starting at minute 7 (not on a quarter boundary) has no mapping.
        var start = new DateTimeOffset(9999, 1, 1, 0, 7, 0, TimeSpan.Zero);
        var end = start.AddMinutes(15);
        var dto = new InstrumentIdDto("DE", start, end);

        Assert.Null(_resolver.Resolve(dto));
    }

    [Fact]
    public void Resolve_ByDto_NullInstrumentReturnsNull()
    {
        Assert.Null(_resolver.Resolve((InstrumentIdDto?)null!));
    }

    [Theory]
    [InlineData("DE.999901010000-999901010015", 0)]
    [InlineData("DE.999901010015-999901010030", 1)]
    [InlineData("DE.999901010030-999901010045", 2)]
    [InlineData("DE.999901010045-999901010100", 3)]
    public void Resolve_ByRoutingKey_MapsQuartersToIndex(string routingKey, int expected)
    {
        Assert.Equal(expected, _resolver.Resolve(routingKey));
    }

    [Fact]
    public void Resolve_ByRoutingKey_HourWindowReturnsNull()
    {
        // 60-minute window (start .. start+60min) on the same day, different hour — the
        // resolver's routing-key form should decline to map it.
        Assert.Null(_resolver.Resolve("DE.999901010000-999901010100"));
    }

    [Fact]
    public void Resolve_ByRoutingKey_MalformedReturnsNull()
    {
        Assert.Null(_resolver.Resolve(""));
        Assert.Null(_resolver.Resolve("garbage"));
        Assert.Null(_resolver.Resolve("DE.nope"));
    }

    private static InstrumentIdDto ToDto(InstrumentId id) =>
        new(id.DeliveryArea.Value, id.DeliveryPeriod.Start, id.DeliveryPeriod.End);
}
