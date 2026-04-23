namespace Bifrost.Contracts.Internal.Events;

public sealed record LifecycleEventDto(
    int SchemaVersion,
    long OrderId,
    string InstrumentId,
    string Side,
    string StrategyName,
    IReadOnlyList<LifecycleHop> Hops);
