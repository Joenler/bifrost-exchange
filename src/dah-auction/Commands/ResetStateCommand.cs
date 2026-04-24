namespace Bifrost.DahAuction.Commands;

/// <summary>
/// Posted by the OnChange subscriber when the round transitions back to
/// <c>IterationOpen</c>. Clears the (team, quarter) bid map and flips the
/// accepting-bids flag to false. Must be idempotent — OnChange may re-enter
/// against an already-empty bid map, so this path must not assume prior state.
/// </summary>
public sealed record ResetStateCommand : IAuctionCommand;
