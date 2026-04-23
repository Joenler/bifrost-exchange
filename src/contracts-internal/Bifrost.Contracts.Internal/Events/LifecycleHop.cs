namespace Bifrost.Contracts.Internal.Events;

public sealed record LifecycleHop(
    HopType Type,
    long LocalNs,
    long? ExchangeNs,
    string? Detail);
