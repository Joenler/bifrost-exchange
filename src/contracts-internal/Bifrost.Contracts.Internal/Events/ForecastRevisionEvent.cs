namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// MC-injected discrete forecast revision (alongside news and shocks) — public,
/// carries no team identity.
/// </summary>
public sealed record ForecastRevisionEvent(
    long NewForecastPriceTicks,
    string Reason,
    long TimestampNs);
