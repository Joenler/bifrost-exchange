namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Exchange-to-trader notification that an order was rejected with a reason.
/// </summary>
public sealed record OrderRejectedEvent(
    long OrderId,
    string ClientId,
    string Reason,
    long TimestampNs);
