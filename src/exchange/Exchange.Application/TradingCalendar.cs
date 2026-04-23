using Bifrost.Exchange.Domain;

namespace Bifrost.Exchange.Application;

// BIFROST: static 5-instrument DE-only registry with synthetic far-future
// delivery date. Phase 02 matcher runs against 1 hour + 4 quarters. The
// synthetic 9999-01-01T00:00Z start keeps DeliveryPeriod.HasExpired false
// regardless of clock during integration tests (physical-delivery semantic:
// expiry fires at Start). Phase 06 orchestrator will replace this when real
// round timelines exist.
public static class TradingCalendar
{
    public static IReadOnlyList<InstrumentId> GenerateInstruments()
    {
        var area = new DeliveryArea("DE");
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new[]
        {
            new InstrumentId(area, new DeliveryPeriod(hourStart,                   hourStart.AddHours(1))),   // hour
            new InstrumentId(area, new DeliveryPeriod(hourStart,                   hourStart.AddMinutes(15))), // Q1
            new InstrumentId(area, new DeliveryPeriod(hourStart.AddMinutes(15),    hourStart.AddMinutes(30))), // Q2
            new InstrumentId(area, new DeliveryPeriod(hourStart.AddMinutes(30),    hourStart.AddMinutes(45))), // Q3
            new InstrumentId(area, new DeliveryPeriod(hourStart.AddMinutes(45),    hourStart.AddHours(1))),    // Q4
        };
    }
}
