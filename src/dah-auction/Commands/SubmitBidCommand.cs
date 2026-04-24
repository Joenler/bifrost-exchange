using Bifrost.Contracts.Internal.Auction;

namespace Bifrost.DahAuction.Commands;

/// <summary>
/// Request from the HTTP endpoint handler to accept a bid matrix for a (team,
/// quarter) pair. Completion is the caller-visible channel: the actor loop
/// sets <see cref="SubmitBidResult.Accepted"/> = true if the gate is open and
/// the matrix replaces any prior submission; false with a reject code if the
/// gate is closed (RoundState != AuctionOpen =&gt; "AuctionNotOpen").
/// </summary>
public sealed record SubmitBidCommand(
    BidMatrixDto BidMatrix,
    TaskCompletionSource<SubmitBidResult> Completion) : IAuctionCommand;
