namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Public forecast update broadcast at the simulator's configured cadence during RoundOpen.
/// Carries no per-team identity — the public fairness invariant.
/// </summary>
public sealed record ForecastUpdateEvent(
    long ForecastPriceTicks,
    long HorizonNs,
    long TimestampNs);
