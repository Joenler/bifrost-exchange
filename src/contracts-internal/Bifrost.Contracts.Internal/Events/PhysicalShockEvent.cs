namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// MC-injected per-quarter physical shock consumed by the imbalance simulator.
/// QuarterIndex is required in production (orchestrator-side enforcement);
/// the simulator asserts on receipt. Persistence is round-scoped or transient.
/// </summary>
public sealed record PhysicalShockEvent(
    int Mw,
    string Label,
    string Persistence,
    int QuarterIndex,
    long TimestampNs);
