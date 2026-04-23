using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Time;

namespace Bifrost.Exchange.Tests.Fixtures;

/// <summary>
/// Factory that composes the production <see cref="ExchangeService"/> + its dependency
/// graph for in-process tests (no RabbitMQ). Returns the service along with the
/// <see cref="CapturingEventPublisher"/> so tests can assert on the captured event
/// stream, and the list of instruments so tests can pick valid targets.
///
/// Used by:
///   - <c>SingleWriterStressTests</c> — drives Parallel.For(0, 100_000, threads=8, ...)
///     through <see cref="ExchangeService.HandleSubmitOrder"/>.
///   - <c>BookReconstructionTests</c> — captures every <c>BookDeltaEvent</c> and replays
///     it in full-sequence order against a shadow <see cref="OrderBook"/>.
/// </summary>
public static class TestHarness
{
    public static (ExchangeService Service, CapturingEventPublisher Publisher, IReadOnlyList<InstrumentId> Instruments, InstrumentRegistry Registry)
        BuildService(IClock clock, IRoundStateSource roundStateSource)
    {
        var instruments = TradingCalendar.GenerateInstruments();

        var engines = instruments
            .Select(id => new MatchingEngine(new OrderBook(id), new MonotonicSequenceGenerator()))
            .ToList();

        var registry = new InstrumentRegistry(engines);

        var rules = new ExchangeRulesConfig(
            TickSize: 1,
            MinQuantity: 1m,
            QuantityStep: 1m,
            MakerFeeRate: 0.01m,
            TakerFeeRate: 0.02m,
            PriceScale: 10);

        var publisher = new CapturingEventPublisher();
        var sequenceTracker = new PublicSequenceTracker();
        var validator = new OrderValidator(rules, registry, clock, roundStateSource);
        var bookPublisher = new BookPublisher(publisher, sequenceTracker);
        var tradePublisher = new TradePublisher(publisher, sequenceTracker, rules);
        var orderIdGenerator = new MonotonicSequenceGenerator();

        var service = new ExchangeService(
            validator,
            bookPublisher,
            tradePublisher,
            registry,
            publisher,
            sequenceTracker,
            orderIdGenerator,
            clock,
            rules);

        return (service, publisher, instruments, registry);
    }
}
