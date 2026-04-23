namespace Bifrost.Exchange.Application.RoundState;

/// <summary>
/// Authoritative BIFROST round-lifecycle enum. Only <see cref="RoundOpen"/> accepts new
/// orders; every other value produces <c>RejectionCode.ExchangeClosed</c> in
/// <see cref="OrderValidator"/>.
/// Cancels are always accepted regardless of state (mass-cancel-on-disconnect invariant).
/// </summary>
public enum RoundState
{
    IterationOpen,
    AuctionOpen,
    AuctionClosed,
    RoundOpen,
    Gate,
    Settled,
    Aborted,
}
