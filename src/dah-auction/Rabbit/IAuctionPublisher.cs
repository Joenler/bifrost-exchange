using Bifrost.Contracts.Internal.Auction;

namespace Bifrost.DahAuction.Rabbit;

/// <summary>
/// Publisher abstraction consumed by the auction write-loop. The production
/// implementation (<see cref="AuctionPublisher"/>) fans out to RabbitMQ;
/// integration tests substitute an in-memory capture version without needing
/// a live broker.
/// </summary>
/// <remarks>
/// All four emission methods are fire-and-forget from the caller's
/// perspective. <see cref="PublishClearingResultAsync"/> returns a
/// <see cref="ValueTask"/> because the production direct-channel path is
/// awaited inside the actor loop's drain body; the three audit-event methods
/// are non-async because the production path queues onto
/// <c>BufferedEventPublisher</c> which is itself fire-and-forget.
/// </remarks>
public interface IAuctionPublisher
{
    /// <summary>
    /// Publish one <see cref="ClearingResultDto"/> envelope on the direct
    /// auction exchange. The caller emits one summary row plus N per-team
    /// rows per quarter, in a contiguous batch.
    /// </summary>
    ValueTask PublishClearingResultAsync(ClearingResultDto payload, CancellationToken ct = default);

    /// <summary>
    /// Publish an <c>auction_bid</c> audit event on the public events bus.
    /// </summary>
    void PublishAuctionBidEvent(BidMatrixDto bid);

    /// <summary>
    /// Publish an <c>auction_cleared</c> audit event on the public events bus.
    /// Carries the per-quarter summary row.
    /// </summary>
    void PublishAuctionClearedEvent(ClearingResultDto summary);

    /// <summary>
    /// Publish an <c>auction_no_cross</c> audit event on the public events
    /// bus, carrying only the failing quarter id.
    /// </summary>
    void PublishAuctionNoCrossEvent(string quarterId);
}
