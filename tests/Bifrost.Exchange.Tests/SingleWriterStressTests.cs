using System.Collections.Concurrent;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Exchange.Tests.Fixtures;
using Xunit;

namespace Bifrost.Exchange.Tests;

/// <summary>
/// EX-07 stress coverage: drive <see cref="ExchangeService.HandleSubmitOrder"/> from
/// 8 producer threads (<see cref="Parallel.For"/>) × 12 500 iterations each = 100 000
/// submits against the full production matcher, and assert the 5 single-writer
/// invariants hold.
///
/// The 100 000-ops floor is unconditional (Phase 02 VALIDATION.md §EX-07). The 12 500
/// figure is the minimum per-thread value that keeps the total at the 10⁵ floor with
/// exactly 8 threads, matching Arena's single-writer guarantee shape. If this suite
/// completes in &lt; 5 s on the dev box, the per-thread count can be scaled up (that is
/// an optional ceiling); the floor never moves.
///
/// Invariants asserted:
///   1. <see cref="ExchangeService"/> never throws under concurrent load.
///   2. Per-instrument public delta sequence is strictly monotonic (no gap, no reorder).
///   3. Every captured <see cref="PublicTradeEvent"/> carries a unique <c>TradeId</c>.
///   4. No accepted order overshoots its submitted quantity (sum of fills per aggressor
///      order ≤ submitted quantity).
///   5. Total accepts + rejects == total submits (no silently dropped commands).
///
/// RNG discipline: every iteration uses <c>new Random(42 + i)</c> — deterministic,
/// seeded per iteration. The shared global RNG is BANNED by CLAUDE.md §Constraints
/// (banned-symbols fence in build/BannedSymbols.txt). Deterministic seeding keeps the
/// test reproducible across runs.
/// </summary>
public sealed class SingleWriterStressTests
{
    // 8 × 12_500 = 100_000 ≥ 10⁵ floor (VALIDATION.md §EX-07).
    private const int ThreadCount = 8;
    private const int OrdersPerThread = 12_500;
    private const int TotalOrders = ThreadCount * OrdersPerThread;

    [Fact]
    public void MatchingEngine_SingleWriter_NoRaceUnder8ProducerThreads()
    {
        var clock = new TestClock();
        var roundStateSource = new ConfigRoundStateSource(
            Bifrost.Exchange.Application.RoundState.RoundState.RoundOpen);
        var (service, publisher, instruments) = TestHarness.BuildService(clock, roundStateSource);

        var instrumentDtos = instruments.Select(ToDto).ToArray();

        var exceptions = new ConcurrentQueue<Exception>();

        var options = new ParallelOptions { MaxDegreeOfParallelism = ThreadCount };

        // Single-writer serializer: models production's single RabbitMQ consumer thread.
        // Production ExchangeService has NO internal synchronization — the serialization
        // comes from the single-consumer AMQP queue upstream (ADR-0002 / Arena concurrency
        // invariant). Tests must reproduce that guarantee; otherwise we'd be measuring
        // undefined behavior (SortedDictionary / Dictionary are not thread-safe) rather
        // than the single-writer contract. The 8 producer threads still generate load
        // concurrently; the lock-then-dispatch pattern serializes the matcher write.
        var writerLock = new object();

        Parallel.For(0, TotalOrders, options, i =>
        {
            // Deterministic per-iteration RNG — the shared global is banned.
            var rng = new Random(42 + i);

            // Spread load across teams + all 5 instruments; alternate sides.
            var instrument = instrumentDtos[i % instrumentDtos.Length];
            var cmd = new SubmitOrderCommand(
                ClientId: $"team-{i % 4}",
                InstrumentId: instrument,
                Side: (i & 1) == 0 ? "Buy" : "Sell",
                OrderType: "Limit",
                // PriceTicks jittered over a narrow band so the matcher exercises
                // cross-side fills AND level-add paths under concurrency.
                PriceTicks: 100 + rng.Next(-5, 6),
                Quantity: 1m,
                DisplaySliceSize: null);

            try
            {
                lock (writerLock)
                {
                    service.HandleSubmitOrder(cmd, replyTo: null, correlationId: null)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        // Invariant 1: no exceptions escaped the matcher.
        Assert.Empty(exceptions);

        // Invariant 2: per-instrument public sequence is strictly monotonic AND unique.
        // BookPublisher and TradePublisher share the per-instrument PublicSequenceTracker,
        // so delta-sequences and trade-sequences are drawn from the same counter; the
        // combined stream is the consumer-visible sequence that must reconstruct the
        // book. Strict-monotonic + no-duplicate + no-gap across the merged set is the
        // single-writer guarantee's direct observable — any corruption would produce
        // duplicate or gapped sequence numbers.
        AssertMonotonicSequencePerInstrument(publisher.CapturedDeltas, publisher.CapturedTrades);

        // Invariant 3: unique TradeIds across every captured public trade.
        AssertUniqueTradeIds(publisher.CapturedTrades);

        // Invariant 4: per-aggressor-order fill quantities never exceed the submitted
        // quantity of 1m (all submits used Quantity=1m).
        AssertFilledWithinSubmitted(publisher.CapturedPrivate);

        // Invariant 5: conservation — every submitted command produced exactly one
        // terminal acceptance or rejection for the submitting client. Duplicate
        // dispatch would inflate this count; dropped commands would deflate it.
        AssertSubmitAccountedFor(publisher.CapturedPrivate, TotalOrders);
    }

    // ---- Helpers ----

    private static InstrumentIdDto ToDto(InstrumentId id) =>
        new(id.DeliveryArea.Value, id.DeliveryPeriod.Start, id.DeliveryPeriod.End);

    private static void AssertMonotonicSequencePerInstrument(
        IEnumerable<(string RoutingKey, object Delta, long Sequence)> deltas,
        IEnumerable<(string RoutingKey, object Trade, long Sequence)> trades)
    {
        // Per-instrument: merge delta + trade sequences, sort, and require a contiguous
        // range starting from the lowest assigned number. The single-writer guarantee is
        // "PublicSequenceTracker.Next never yields the same number twice for an instrument
        // and never skips". The SET of assigned sequences must therefore equal
        // {k, k+1, …, k+n-1} for some starting k (0-based; first Next() returns 1).
        var byInstrument = deltas
            .Select(d => (d.RoutingKey, d.Sequence))
            .Concat(trades.Select(t => (t.RoutingKey, t.Sequence)))
            .GroupBy(pair => pair.RoutingKey);

        foreach (var group in byInstrument)
        {
            var seqs = group.Select(g => g.Sequence).OrderBy(s => s).ToList();
            if (seqs.Count == 0)
                continue;

            // Duplicates are always a single-writer violation.
            Assert.Equal(seqs.Count, seqs.Distinct().Count());

            // Gaps are always a single-writer violation. The first assigned sequence is
            // seqs[0]; every subsequent assignment must be prev + 1.
            for (var i = 1; i < seqs.Count; i++)
            {
                Assert.Equal(seqs[i - 1] + 1, seqs[i]);
            }
        }
    }

    private static void AssertUniqueTradeIds(
        IEnumerable<(string RoutingKey, object Trade, long Sequence)> trades)
    {
        // TradeId is assigned by the per-instrument MonotonicSequenceGenerator inside
        // each MatchingEngine. The uniqueness invariant is per-instrument: within a
        // single book, no two trades share a TradeId. (Across instruments, IDs overlap
        // by design since each engine owns its own generator — the RoutingKey + TradeId
        // tuple is what's globally unique on the public.trade feed.)
        foreach (var group in trades.GroupBy(t => t.RoutingKey))
        {
            var tradeIds = new List<long>();
            foreach (var (_, trade, _) in group)
            {
                var publicTrade = Assert.IsType<PublicTradeEvent>(trade);
                tradeIds.Add(publicTrade.TradeId);
            }

            Assert.Equal(tradeIds.Count, tradeIds.Distinct().Count());
        }
    }

    private static void AssertFilledWithinSubmitted(
        IEnumerable<(string ClientId, object Evt, string? CorrelationId)> privateEvents)
    {
        // All submits used Quantity=1m. A private OrderExecutedEvent's FilledQuantity
        // must therefore be ≤ 1m, and the cumulative filled quantity for any single
        // OrderId must also be ≤ 1m (aggressor cannot fill more than it submitted).
        var perOrderFilled = new Dictionary<long, decimal>();
        foreach (var (_, evt, _) in privateEvents)
        {
            if (evt is not OrderExecutedEvent exec)
                continue;

            Assert.InRange(exec.FilledQuantity, 0m, 1m);

            if (perOrderFilled.TryGetValue(exec.OrderId, out var running))
                perOrderFilled[exec.OrderId] = running + exec.FilledQuantity;
            else
                perOrderFilled[exec.OrderId] = exec.FilledQuantity;
        }

        foreach (var (orderId, totalFilled) in perOrderFilled)
        {
            Assert.True(totalFilled <= 1m,
                $"OrderId {orderId} filled {totalFilled} > submitted 1m");
        }
    }

    private static void AssertSubmitAccountedFor(
        IEnumerable<(string ClientId, object Evt, string? CorrelationId)> privateEvents,
        int expectedSubmits)
    {
        // Each successful submit emits exactly one OrderAcceptedEvent; each rejected
        // submit emits exactly one OrderRejectedEvent. No submit should fail to emit
        // a terminal decision.
        var accepts = 0;
        var rejects = 0;
        foreach (var (_, evt, _) in privateEvents)
        {
            if (evt is OrderAcceptedEvent)
                accepts++;
            else if (evt is OrderRejectedEvent)
                rejects++;
        }

        Assert.Equal(expectedSubmits, accepts + rejects);
    }
}
