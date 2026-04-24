using Bifrost.Exchange.Application;
using Bifrost.Exchange.Domain;
using Xunit;

namespace Bifrost.Exchange.Tests;

/// <summary>
/// Covers the DAH auction's quarter-hour filter helper on
/// <see cref="InstrumentRegistry"/> over the 5-instrument
/// <see cref="TradingCalendar"/> fixture. Must return exactly 4
/// quarter-hour instruments in ascending-start deterministic order;
/// the one-hour instrument is excluded by construction.
/// </summary>
public sealed class InstrumentRegistryTests
{
    [Fact]
    public void GetQuarterInstruments_ReturnsFourQuarterOnly_InAscendingStartOrder()
    {
        // Build a registry whose key-set matches the canonical 1 + 4 TradingCalendar layout.
        // This test doesn't exercise matching behavior, only the Keys-based filter —
        // we just need engines keyed off the right InstrumentIds.
        var instruments = TradingCalendar.GenerateInstruments();
        var engines = instruments
            .Select(id => new MatchingEngine(new OrderBook(id), new MonotonicSequenceGenerator()))
            .ToList();
        var registry = new InstrumentRegistry(engines);

        var quarters = registry.GetQuarterInstruments();

        Assert.Equal(4, quarters.Count);

        foreach (var id in quarters)
        {
            Assert.Equal(TimeSpan.FromMinutes(15), id.DeliveryPeriod.End - id.DeliveryPeriod.Start);
        }

        // Ascending start ordering.
        for (int i = 1; i < quarters.Count; i++)
        {
            Assert.True(
                quarters[i - 1].DeliveryPeriod.Start <= quarters[i].DeliveryPeriod.Start,
                $"Quarter at index {i} ({quarters[i].DeliveryPeriod.Start}) comes before index {i - 1} ({quarters[i - 1].DeliveryPeriod.Start}) — GetQuarterInstruments must sort ascending by Start.");
        }

        // Hour instrument (60 min) is excluded.
        Assert.DoesNotContain(quarters, id =>
            (id.DeliveryPeriod.End - id.DeliveryPeriod.Start) == TimeSpan.FromMinutes(60));
    }
}
