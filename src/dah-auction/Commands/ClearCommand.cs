namespace Bifrost.DahAuction.Commands;

/// <summary>
/// Posted by the <c>IRoundStateSource.OnChange</c> subscriber when the round
/// transitions <c>AuctionOpen -&gt; AuctionClosed</c>. The actor loop snapshots
/// the bid map, computes clearing per QH via
/// <c>Bifrost.DahAuction.Clearing.UniformPriceClearing.Compute</c>
/// (implemented separately), publishes <c>ClearingResult</c> envelopes and
/// <c>Event</c> rows, and flips the accepting-bids flag to false.
/// </summary>
public sealed record ClearCommand(long SnapshotTimestampNs) : IAuctionCommand;
