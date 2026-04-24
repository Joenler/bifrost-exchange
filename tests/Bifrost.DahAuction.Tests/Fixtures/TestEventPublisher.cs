using System.Collections.Concurrent;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.DahAuction.Rabbit;

namespace Bifrost.DahAuction.Tests.Fixtures;

/// <summary>
/// Captured emission record. Mirrors the production
/// (exchange, routingKey, payload) tuple but holds the payload as the
/// concrete DTO (or anonymous object for the no-cross body) so tests can
/// assert on type and field values directly without re-deserializing JSON.
/// </summary>
public sealed record CapturedAuctionMessage(string Exchange, string RoutingKey, object Payload);

/// <summary>
/// Test-only <see cref="IAuctionPublisher"/> that captures envelopes
/// in-memory for assertion. Substituted for the production
/// <c>AuctionPublisher</c> in <see cref="TestAuctionHost"/> so integration
/// tests can drive the same actor-loop + clearing pipeline without needing
/// a live RabbitMQ broker.
/// </summary>
/// <remarks>
/// Thread-safe via <see cref="ConcurrentBag{T}"/>: the auction write-loop
/// drain thread is the only writer in production, but the integration tests
/// observe captures from the test thread after queuing transitions. Keeping
/// the bag concurrent removes any race between drain-thread emission and
/// caller assertion.
/// </remarks>
public sealed class TestAuctionPublisher : IAuctionPublisher
{
    /// <summary>
    /// Captured auction emissions in arrival order. Use the convenience helpers
    /// or LINQ-filter on <see cref="CapturedAuctionMessage.RoutingKey"/> to
    /// assert specific routing keys.
    /// </summary>
    public ConcurrentBag<CapturedAuctionMessage> Captured { get; } = new();

    public ValueTask PublishClearingResultAsync(ClearingResultDto payload, CancellationToken ct = default)
    {
        Captured.Add(new CapturedAuctionMessage(
            Exchange: "bifrost.auction",
            RoutingKey: $"bifrost.auction.cleared.{payload.QuarterId}",
            Payload: payload));
        return ValueTask.CompletedTask;
    }

    public void PublishAuctionBidEvent(BidMatrixDto bid) =>
        Captured.Add(new CapturedAuctionMessage(
            Exchange: "bifrost.public",
            RoutingKey: "events.auction.bid",
            Payload: bid));

    public void PublishAuctionClearedEvent(ClearingResultDto summary) =>
        Captured.Add(new CapturedAuctionMessage(
            Exchange: "bifrost.public",
            RoutingKey: "events.auction.cleared",
            Payload: summary));

    public void PublishAuctionNoCrossEvent(string quarterId) =>
        Captured.Add(new CapturedAuctionMessage(
            Exchange: "bifrost.public",
            RoutingKey: "events.auction.no_cross",
            Payload: new { quarterId }));
}
