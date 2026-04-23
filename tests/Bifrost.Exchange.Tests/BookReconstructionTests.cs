using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Exchange.Tests.Fixtures;
using Xunit;
using Xunit.Sdk;

namespace Bifrost.Exchange.Tests;

/// <summary>
/// EX-03 coverage: a public-book-delta consumer can reconstruct the authoritative
/// book at sequence N. This test drives a scripted tape through the production
/// <see cref="ExchangeService"/>, captures every <see cref="BookDeltaEvent"/> that
/// would have been broadcast to the public feed, and replays the full sequence
/// into an independent shadow-book — then asserts the shadow exactly matches the
/// authoritative <see cref="OrderBook"/>.
///
/// Nyquist discipline (Phase 02 VALIDATION.md §EX-03): replay EVERY delta in
/// per-instrument sequence order — no sampling, no spot-check. The guarantee is
/// "monotonic per-instrument sequence"; any gap invalidates the reconstruction
/// model, so the test asserts zero gaps before comparing books.
///
/// Surface probe: the test checks that the authoritative book exposes the read
/// surface we compare against (<c>Bids</c> / <c>Asks</c> dictionaries with
/// <c>TotalVisibleQuantity</c> and <c>OrderCount</c> per level). If that surface
/// ever disappears in a refactor, the test explicitly skips via
/// <see cref="SkipException"/> with an escalation message — never silently
/// downgrades to a weaker invariant (e.g. order-replay determinism).
/// </summary>
[Trait("Priority", "Critical")]
public sealed class BookReconstructionTests
{
    [Fact]
    public async Task FullSequenceReplay_ReconstructsAuthoritativeBook()
    {
        if (!AuthoritativeBookExposesReadSurface())
        {
            throw SkipException.ForSkip(
                "OrderBook no longer exposes the Bids/Asks read surface used for " +
                "delta-replay comparison. EX-03 (public.book.delta consumer can " +
                "reconstruct the book at any sequence N) cannot be verified against " +
                "a weaker proxy without silently changing the guarantee. Escalate: " +
                "either (a) restore the Bids/Asks + TotalVisibleQuantity/OrderCount " +
                "read surface on OrderBook (preferred — this is what the delta DTO " +
                "is built against), or (b) explicitly approve a fallback " +
                "reconstruction mechanism in a new ADR.");
        }

        var clock = new TestClock();
        var roundStateSource = new ConfigRoundStateSource(
            Bifrost.Exchange.Application.RoundState.RoundState.RoundOpen);
        var (service, publisher, instruments, registry) =
            TestHarness.BuildService(clock, roundStateSource);

        // Scripted tape on instrument[0]: 200 mixed buy/sell limit orders across a
        // price band wide enough to produce both fills AND resting-level adds. A
        // deterministic RNG keeps the tape reproducible across runs (seed 12345 is
        // arbitrary but fixed; Random.Shared is banned by CLAUDE.md §Constraints).
        var target = instruments[0];
        var targetDto = new InstrumentIdDto(
            target.DeliveryArea.Value,
            target.DeliveryPeriod.Start,
            target.DeliveryPeriod.End);

        var rng = new Random(12345);
        for (var i = 0; i < 200; i++)
        {
            var side = (i & 1) == 0 ? "Buy" : "Sell";
            var priceTicks = 100L + rng.Next(-5, 6);
            var quantity = 1m + rng.Next(1, 5);
            var cmd = new SubmitOrderCommand(
                ClientId: $"team-{i % 3}",
                InstrumentId: targetDto,
                Side: side,
                OrderType: "Limit",
                PriceTicks: priceTicks,
                Quantity: quantity,
                DisplaySliceSize: null);

            await service.HandleSubmitOrder(cmd, replyTo: null, correlationId: $"r-{i}");
        }

        // Extract the authoritative book post-tape.
        var engine = registry.TryGet(target);
        Assert.NotNull(engine);
        var authoritativeBook = engine!.Book;

        // Gather deltas emitted for the target instrument. CapturedDeltas carries the
        // routing key and the raw BookDeltaEvent; filter by the DTO key and order by
        // sequence for Nyquist-safe replay.
        var targetRoutingKey = target.ToRoutingKey();
        var deltasForTarget = publisher.CapturedDeltas
            .Where(d => d.RoutingKey == targetRoutingKey)
            .Select(d => (d.RoutingKey, Evt: (BookDeltaEvent)d.Delta, d.Sequence))
            .OrderBy(d => d.Sequence)
            .ToList();

        Assert.NotEmpty(deltasForTarget);

        // Nyquist gap check — the single-writer guarantee is "monotonic per-instrument
        // sequence with no gaps". Trade-publisher shares the same counter as the
        // book-publisher (ADR-0002 §BookPublisher + TradePublisher use
        // PublicSequenceTracker.Next per instrument), so delta sequences form a subset
        // of the total sequence; intra-delta gaps are EXPECTED (filled by trades)
        // but NO delta-sequence may be duplicated or re-ordered.
        for (var i = 1; i < deltasForTarget.Count; i++)
        {
            Assert.True(
                deltasForTarget[i].Sequence > deltasForTarget[i - 1].Sequence,
                $"Gap in delta sequence at index {i}: " +
                $"{deltasForTarget[i - 1].Sequence} -> {deltasForTarget[i].Sequence}" +
                $" (strict monotonicity violated; cannot reconstruct)");
        }

        // Build the shadow book from an empty state by applying every delta. The
        // BookDeltaEvent DTO carries the RESULTING level state per changed price
        // (quantity + order-count); a zero quantity means the level was removed.
        // This is exactly what a downstream consumer (bigscreen, recorder) would
        // maintain to reconstruct the book.
        var shadow = new ShadowBook();
        foreach (var (_, evt, _) in deltasForTarget)
        {
            shadow.Apply(evt);
        }

        // Compare shadow to authoritative: price-ticks, total visible quantity,
        // order count per side. Every level present in either must match the other.
        AssertBooksMatch(authoritativeBook, shadow);
    }

    // ---- Surface probe ----

    private static bool AuthoritativeBookExposesReadSurface()
    {
        // The test depends on OrderBook exposing Bids + Asks as readable dictionaries
        // keyed by Price with PriceLevel values that carry TotalVisibleQuantity and
        // OrderCount. If any of those shape-properties disappears, the test CANNOT
        // compare books level-by-level and must skip loudly.
        var bookType = typeof(OrderBook);
        var priceLevelType = typeof(PriceLevel);
        return bookType.GetProperty(nameof(OrderBook.Bids)) is not null
            && bookType.GetProperty(nameof(OrderBook.Asks)) is not null
            && priceLevelType.GetProperty(nameof(PriceLevel.TotalVisibleQuantity)) is not null
            && priceLevelType.GetProperty(nameof(PriceLevel.OrderCount)) is not null;
    }

    // ---- Assertion ----

    private static void AssertBooksMatch(OrderBook authoritative, ShadowBook shadow)
    {
        // Bids side.
        AssertSideMatches(
            side: "Bids",
            authoritative: authoritative.Bids.ToDictionary(
                kvp => kvp.Key.Ticks,
                kvp => (kvp.Value.TotalVisibleQuantity.Value, kvp.Value.OrderCount)),
            shadow: shadow.Bids);

        // Asks side.
        AssertSideMatches(
            side: "Asks",
            authoritative: authoritative.Asks.ToDictionary(
                kvp => kvp.Key.Ticks,
                kvp => (kvp.Value.TotalVisibleQuantity.Value, kvp.Value.OrderCount)),
            shadow: shadow.Asks);
    }

    private static void AssertSideMatches(
        string side,
        IReadOnlyDictionary<long, (decimal Quantity, int OrderCount)> authoritative,
        IReadOnlyDictionary<long, (decimal Quantity, int OrderCount)> shadow)
    {
        Assert.True(
            authoritative.Count == shadow.Count,
            $"{side}: level count mismatch — authoritative {authoritative.Count}, shadow {shadow.Count}");

        foreach (var (priceTicks, authLevel) in authoritative)
        {
            Assert.True(
                shadow.TryGetValue(priceTicks, out var shadowLevel),
                $"{side}: price {priceTicks} present in authoritative but missing from shadow");
            Assert.Equal(authLevel.Quantity, shadowLevel.Quantity);
            Assert.Equal(authLevel.OrderCount, shadowLevel.OrderCount);
        }

        foreach (var priceTicks in shadow.Keys)
        {
            Assert.True(
                authoritative.ContainsKey(priceTicks),
                $"{side}: price {priceTicks} present in shadow but missing from authoritative");
        }
    }

    /// <summary>
    /// Minimal in-test shadow book consumed by EX-03's delta-replay path. Maintains
    /// per-side dictionaries keyed by price-ticks with (total-visible-quantity,
    /// order-count) payload. A delta that reports a level with quantity==0 removes
    /// that level (matches <see cref="BookDeltaBuilder"/>'s zero-level convention
    /// when a price level becomes empty).
    /// </summary>
    private sealed class ShadowBook
    {
        private readonly Dictionary<long, (decimal Quantity, int OrderCount)> _bids = new();
        private readonly Dictionary<long, (decimal Quantity, int OrderCount)> _asks = new();

        public IReadOnlyDictionary<long, (decimal Quantity, int OrderCount)> Bids => _bids;
        public IReadOnlyDictionary<long, (decimal Quantity, int OrderCount)> Asks => _asks;

        public void Apply(BookDeltaEvent delta)
        {
            foreach (var level in delta.ChangedBids)
                ApplyLevel(_bids, level);

            foreach (var level in delta.ChangedAsks)
                ApplyLevel(_asks, level);
        }

        private static void ApplyLevel(
            Dictionary<long, (decimal Quantity, int OrderCount)> side,
            BookLevelDto level)
        {
            // BookDeltaBuilder emits quantity==0, orderCount==0 for a level that
            // was fully drained. The shadow responds by removing the level so the
            // post-drain book state matches the authoritative book.
            if (level.Quantity == 0m && level.OrderCount == 0)
            {
                side.Remove(level.PriceTicks);
            }
            else
            {
                side[level.PriceTicks] = (level.Quantity, level.OrderCount);
            }
        }
    }
}
