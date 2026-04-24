using System.Net;
using System.Net.Http.Json;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.DahAuction.Tests.Fixtures;
using Bifrost.Exchange.Application;
using Xunit;
using RoundStateEnum = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.DahAuction.Tests;

/// <summary>
/// Integration tests over the in-process WebApplication harness. Each test
/// brings up its own <see cref="TestAuctionHost"/> and drives the lifecycle
/// via <see cref="MockRoundStateSource.TransitionTo"/>. Assertions read
/// captured publisher emissions via <see cref="TestAuctionHost.CapturedMessages"/>.
/// </summary>
public sealed class AuctionLifecycleIntegrationTests
{
    /// <summary>
    /// First quarter-hour instrument id (Q1 by TradingCalendar ordering).
    /// Matches the validator's registry view of GetQuarterInstruments().
    /// </summary>
    private static string FirstQuarterId() =>
        TradingCalendar.GenerateInstruments()
            .First(i => (i.DeliveryPeriod.End - i.DeliveryPeriod.Start) == TimeSpan.FromMinutes(15))
            .ToString();

    private static BidMatrixDto BuildBid(
        string team,
        string quarter,
        BidStepDto[]? buys = null,
        BidStepDto[]? sells = null) =>
        new(team, quarter,
            buys ?? new BidStepDto[] { new(100L, 30L) },
            sells ?? Array.Empty<BidStepDto>());

    [Fact]
    public async Task FullSevenStateCycle_AcceptsOnlyInAuctionOpen_ClearsOnce_EmptyOnReset()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.IterationOpen);
        var client = host.Client;
        var mock = host.MockRoundState;

        var qh = FirstQuarterId();

        // IterationOpen: POST must be rejected with AuctionNotOpen.
        var initial = await client.PostAsJsonAsync("/auction/bid", BuildBid("alpha", qh), ct);
        Assert.Equal(HttpStatusCode.BadRequest, initial.StatusCode);
        Assert.Contains("AuctionNotOpen", await initial.Content.ReadAsStringAsync(ct));

        // -> AuctionOpen: POST must be accepted. The OpenBidsCommand is FIFO-
        // ordered ahead of the SubmitBidCommand so the actor loop always sees
        // _acceptingBids = true before processing the bid.
        mock.TransitionTo(RoundStateEnum.AuctionOpen);
        var accepted = await client.PostAsJsonAsync("/auction/bid", BuildBid("alpha", qh), ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // -> AuctionClosed: clearing fires. The single alpha bid (buy-only)
        // has no counterpart on any QH so all 4 QHs land in the no-cross
        // branch: 4 summaries on bifrost.auction + 4 no-cross audit events on
        // bifrost.public; zero auction_cleared events; zero per-team rows.
        mock.TransitionTo(RoundStateEnum.AuctionClosed);
        await WaitForClearingSummariesAsync(host, expectedCount: 4, ct: ct);

        var afterFirstClear = host.CapturedMessages.ToList();
        Assert.Equal(4, afterFirstClear.Count(m => m.RoutingKey.StartsWith(
            "bifrost.auction.cleared.", StringComparison.Ordinal)));
        Assert.Equal(4, afterFirstClear.Count(m => m.RoutingKey == "events.auction.no_cross"));
        Assert.Equal(0, afterFirstClear.Count(m => m.RoutingKey == "events.auction.cleared"));

        // Subsequent POST after clearing must reject (acceptingBids flipped to
        // false at the end of ProcessClearAsync).
        var rejectedAfterClose = await client.PostAsJsonAsync("/auction/bid", BuildBid("alpha", qh), ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedAfterClose.StatusCode);
        Assert.Contains("AuctionNotOpen", await rejectedAfterClose.Content.ReadAsStringAsync(ct));

        // -> RoundOpen | Gate | Settled | Aborted: all reject AuctionNotOpen.
        foreach (var s in new[]
        {
            RoundStateEnum.RoundOpen,
            RoundStateEnum.Gate,
            RoundStateEnum.Settled,
            RoundStateEnum.Aborted,
        })
        {
            mock.TransitionTo(s);
            var r = await client.PostAsJsonAsync("/auction/bid", BuildBid("alpha", qh), ct);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("AuctionNotOpen", await r.Content.ReadAsStringAsync(ct));
        }

        // -> IterationOpen: bid map cleared. We probe by going AuctionOpen ->
        // AuctionClosed again and asserting clearing produces another 4 no-cross
        // summaries. The auction_bid event count stays at 1 — proving the bid
        // map was wiped (no second submission across the lifecycle).
        mock.TransitionTo(RoundStateEnum.IterationOpen);
        mock.TransitionTo(RoundStateEnum.AuctionOpen);
        mock.TransitionTo(RoundStateEnum.AuctionClosed);
        await WaitForClearingSummariesAsync(host, expectedCount: 8, ct: ct);

        var afterSecondClear = host.CapturedMessages.ToList();
        Assert.Equal(8, afterSecondClear.Count(m => m.RoutingKey.StartsWith(
            "bifrost.auction.cleared.", StringComparison.Ordinal)));
        Assert.Equal(8, afterSecondClear.Count(m => m.RoutingKey == "events.auction.no_cross"));
        Assert.Equal(0, afterSecondClear.Count(m => m.RoutingKey == "events.auction.cleared"));
        // Only ONE auction_bid was ever submitted (during the first AuctionOpen
        // window). After ResetStateCommand, the map is empty and the second
        // AuctionOpen window had no POSTs.
        Assert.Equal(1, afterSecondClear.Count(m => m.RoutingKey == "events.auction.bid"));
    }

    [Fact]
    public async Task ThreeTeams_FourMessagesOnAuctionCleared_OnQ1()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var client = host.Client;
        var mock = host.MockRoundState;
        var qh = FirstQuarterId();

        // Three teams that cross on Q1.
        //   alpha buy: (100, 30)
        //   beta  buy: (90, 40)
        //   gamma sell: (70, 80)
        // Aggregate demand descending: 30@100, 40@90 -> 70 total.
        // Aggregate supply ascending:  80@70.
        // Crossing search: lowest p where S(p) >= D(p) with positive volume.
        //   At p=70: D=70, S=80. matched = min(70, 80) = 70.
        // Awards: alpha +30, beta +40, gamma -70 (sell side fills 70 of 80).
        var resp1 = await client.PostAsJsonAsync("/auction/bid", new BidMatrixDto(
            "alpha", qh,
            new BidStepDto[] { new(100L, 30L) },
            Array.Empty<BidStepDto>()), ct);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        var resp2 = await client.PostAsJsonAsync("/auction/bid", new BidMatrixDto(
            "beta", qh,
            new BidStepDto[] { new(90L, 40L) },
            Array.Empty<BidStepDto>()), ct);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        var resp3 = await client.PostAsJsonAsync("/auction/bid", new BidMatrixDto(
            "gamma", qh,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(70L, 80L) }), ct);
        Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);

        mock.TransitionTo(RoundStateEnum.AuctionClosed);
        await WaitForClearingSummariesAsync(host, expectedCount: 4, ct: ct);

        // Filter ClearingResultDto messages on Q1 (direct bus only).
        var q1Messages = host.CapturedMessages
            .Where(m => m.RoutingKey == $"bifrost.auction.cleared.{qh}")
            .Select(m => (ClearingResultDto)m.Payload)
            .ToList();

        // Expected: 1 summary + 3 per-team rows = 4.
        Assert.Equal(4, q1Messages.Count);
        Assert.Single(q1Messages, p => p.TeamName is null && p.AwardedQuantityTicks == 0L);
        Assert.Contains(q1Messages, p => p.TeamName == "alpha" && p.AwardedQuantityTicks == 30L);
        Assert.Contains(q1Messages, p => p.TeamName == "beta" && p.AwardedQuantityTicks == 40L);
        Assert.Contains(q1Messages, p => p.TeamName == "gamma" && p.AwardedQuantityTicks == -70L);

        // All rows on Q1 share the same clearing price (uniform-price rule).
        Assert.True(q1Messages.All(p => p.ClearingPriceTicks == 70L));

        // The other 3 QHs were no-cross (no bids) — assert one summary each
        // with price=0 and no per-team rows.
        var nonQ1Messages = host.CapturedMessages
            .Where(m => m.RoutingKey.StartsWith("bifrost.auction.cleared.", StringComparison.Ordinal)
                && m.RoutingKey != $"bifrost.auction.cleared.{qh}")
            .Select(m => (ClearingResultDto)m.Payload)
            .ToList();
        Assert.Equal(3, nonQ1Messages.Count);
        Assert.All(nonQ1Messages, p =>
        {
            Assert.Null(p.TeamName);
            Assert.Equal(0L, p.ClearingPriceTicks);
            Assert.Equal(0L, p.AwardedQuantityTicks);
        });
    }

    [Fact]
    public async Task ReplaceOnDuplicate_ClearingUsesSecondMatrix()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var client = host.Client;
        var mock = host.MockRoundState;
        var qh = FirstQuarterId();

        // First alpha submission: buy 5 @ 100. Combined with gamma's 50@50 supply
        // this would clear at p*=50 with awards alpha +5 / gamma -5 (matched
        // volume = min(5, 50) = 5).
        var r1 = await client.PostAsJsonAsync("/auction/bid", new BidMatrixDto(
            "alpha", qh,
            new BidStepDto[] { new(100L, 5L) },
            Array.Empty<BidStepDto>()), ct);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var r2 = await client.PostAsJsonAsync("/auction/bid", new BidMatrixDto(
            "gamma", qh,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(50L, 50L) }), ct);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        // Second alpha submission: buy 20 @ 100. With replace-on-duplicate
        // semantics, alpha's first matrix (5@100) is fully discarded and ONLY
        // the second matrix (20@100) is in the map at clearing time.
        //   After replace: alpha (buy 20 @ 100) + gamma (sell 50 @ 50).
        //     At p=50: D=20, S=50. S>=D, D>0 -> p*=50. matched = min(20, 50) = 20.
        //   Awards: alpha +20, gamma -20.
        // The award magnitude (20 vs 5) is what disambiguates between
        // first-matrix-only and second-matrix-only behaviour. If the first
        // matrix had been retained instead of replaced, alpha would receive
        // exactly +5 (its first matrix's quantity).
        var r3 = await client.PostAsJsonAsync("/auction/bid", new BidMatrixDto(
            "alpha", qh,
            new BidStepDto[] { new(100L, 20L) },
            Array.Empty<BidStepDto>()), ct);
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);

        mock.TransitionTo(RoundStateEnum.AuctionClosed);
        await WaitForClearingSummariesAsync(host, expectedCount: 4, ct: ct);

        var q1 = host.CapturedMessages
            .Where(m => m.RoutingKey == $"bifrost.auction.cleared.{qh}")
            .Select(m => (ClearingResultDto)m.Payload)
            .ToList();
        // 1 summary + 2 per-team = 3.
        Assert.Equal(3, q1.Count);
        // The disambiguating numeric assertion: alpha = +20 (NOT +5), gamma
        // = -20 (NOT -5).
        Assert.Contains(q1, p => p.TeamName == "alpha" && p.AwardedQuantityTicks == 20L);
        Assert.Contains(q1, p => p.TeamName == "gamma" && p.AwardedQuantityTicks == -20L);

        // Cross-check: both alpha POSTs produced their own audit rows (the
        // bid event fires per accepted submission, before clearing). Check by
        // matching specific quantities rather than ordering — the test
        // publisher's ConcurrentBag does not guarantee insertion order on
        // enumeration, so a Last() against it would be unreliable.
        var alphaBidEvents = host.CapturedMessages
            .Where(m => m.RoutingKey == "events.auction.bid"
                && m.Payload is BidMatrixDto bm && bm.TeamName == "alpha")
            .Select(m => (BidMatrixDto)m.Payload)
            .ToList();
        Assert.Equal(2, alphaBidEvents.Count);
        Assert.Contains(alphaBidEvents, bm =>
            bm.BuySteps.Length == 1
            && bm.BuySteps[0].PriceTicks == 100L
            && bm.BuySteps[0].QuantityTicks == 5L);
        Assert.Contains(alphaBidEvents, bm =>
            bm.BuySteps.Length == 1
            && bm.BuySteps[0].PriceTicks == 100L
            && bm.BuySteps[0].QuantityTicks == 20L);
    }

    [Fact]
    public async Task ZeroBidTeam_NoRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var client = host.Client;
        var mock = host.MockRoundState;
        var qh = FirstQuarterId();

        // alpha + beta submit; gamma never POSTs anything.
        await client.PostAsJsonAsync("/auction/bid", new BidMatrixDto(
            "alpha", qh,
            new BidStepDto[] { new(100L, 10L) },
            Array.Empty<BidStepDto>()), ct);
        await client.PostAsJsonAsync("/auction/bid", new BidMatrixDto(
            "beta", qh,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(50L, 10L) }), ct);

        mock.TransitionTo(RoundStateEnum.AuctionClosed);
        await WaitForClearingSummariesAsync(host, expectedCount: 4, ct: ct);

        // Filter every ClearingResultDto across the bus and assert no row
        // ever references a team named "gamma".
        var allClearingResults = host.CapturedMessages
            .Where(m => m.Payload is ClearingResultDto)
            .Select(m => (ClearingResultDto)m.Payload)
            .ToList();
        Assert.DoesNotContain(allClearingResults, r => r.TeamName == "gamma");
    }

    [Fact]
    public async Task NoCross_OnEmptyQuarter_EmitsSingleSummaryAndNoCrossEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var mock = host.MockRoundState;

        // No bids submitted on any QH. All 4 QHs hit the no-cross branch.
        mock.TransitionTo(RoundStateEnum.AuctionClosed);
        await WaitForClearingSummariesAsync(host, expectedCount: 4, ct: ct);

        // Expect 4 summary messages on bifrost.auction.cleared.<qh> + 4
        // events.auction.no_cross audit events; zero per-team ClearingResult
        // rows; zero events.auction.cleared events.
        var summaryRows = host.CapturedMessages
            .Where(m => m.RoutingKey.StartsWith("bifrost.auction.cleared.", StringComparison.Ordinal))
            .Select(m => (ClearingResultDto)m.Payload)
            .ToList();
        Assert.Equal(4, summaryRows.Count);
        Assert.All(summaryRows, p =>
        {
            Assert.Null(p.TeamName);
            Assert.Equal(0L, p.AwardedQuantityTicks);
            Assert.Equal(0L, p.ClearingPriceTicks);
        });

        Assert.Equal(4, host.CapturedMessages.Count(m => m.RoutingKey == "events.auction.no_cross"));
        Assert.Equal(0, host.CapturedMessages.Count(m => m.RoutingKey == "events.auction.cleared"));
    }

    /// <summary>
    /// Poll the publisher capture stream until at least
    /// <paramref name="expectedCount"/> summary rows on
    /// <c>bifrost.auction.cleared.*</c> are present, or a generous timeout
    /// expires. The actor loop processes the ClearCommand on its own thread;
    /// transitions are queued synchronously by the test thread but the drain
    /// happens asynchronously.
    /// </summary>
    private static async Task WaitForClearingSummariesAsync(
        TestAuctionHost host,
        int expectedCount,
        CancellationToken ct,
        int budgetMs = 3000)
    {
        const int stepMs = 10;
        int steps = budgetMs / stepMs;
        for (int i = 0; i < steps; i++)
        {
            int count = host.CapturedMessages.Count(m =>
                m.RoutingKey.StartsWith("bifrost.auction.cleared.", StringComparison.Ordinal));
            if (count >= expectedCount) return;
            await Task.Delay(stepMs, ct);
        }
        // Final assertion if we time out — surfaces a clear failure message.
        int finalCount = host.CapturedMessages.Count(m =>
            m.RoutingKey.StartsWith("bifrost.auction.cleared.", StringComparison.Ordinal));
        Assert.Fail(
            $"Timed out waiting for {expectedCount} clearing summary rows; observed {finalCount}");
    }
}
