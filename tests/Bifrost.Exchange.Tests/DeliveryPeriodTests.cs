using Bifrost.Exchange.Domain;
using Xunit;

namespace Bifrost.Exchange.Tests;

public sealed class DeliveryPeriodTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End   = new(2026, 1, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HasExpired_IsFalse_BeforeStart()
    {
        var period = new DeliveryPeriod(Start, End);
        var oneSecondBeforeStart = Start.AddSeconds(-1);

        Assert.False(period.HasExpired(oneSecondBeforeStart));
    }

    [Fact]
    public void HasExpired_IsTrue_AtStart()
    {
        // Physical delivery begins at Start — the product stops being tradable at this instant.
        var period = new DeliveryPeriod(Start, End);

        Assert.True(period.HasExpired(Start));
    }

    [Fact]
    public void HasExpired_IsTrue_DuringDeliveryWindow()
    {
        // Mid-delivery the product is locked — intraday trading is already closed.
        var period = new DeliveryPeriod(Start, End);
        var midDelivery = Start.AddMinutes(30);

        Assert.True(period.HasExpired(midDelivery));
    }

    [Fact]
    public void HasExpired_IsTrue_AfterEnd()
    {
        var period = new DeliveryPeriod(Start, End);
        var afterEnd = End.AddSeconds(1);

        Assert.True(period.HasExpired(afterEnd));
    }
}
