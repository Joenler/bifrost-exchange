namespace Bifrost.DahAuction.Commands;

/// <summary>
/// Posted by the OnChange subscriber when the round transitions INTO
/// <c>AuctionOpen</c>. Flips the accepting-bids flag to true; the bid map
/// stays empty (the previous round's matrices were cleared at
/// IterationOpen).
/// </summary>
public sealed record OpenBidsCommand : IAuctionCommand;
