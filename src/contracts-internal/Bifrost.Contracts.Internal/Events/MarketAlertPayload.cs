namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Wire DTO for publications on bifrost.events.v1/events.market_alert. Severity
/// is always "urgent" when constructed from AlertUrgentCmd today, but the field
/// exists for future non-urgent alert expansion without a DTO rev.
/// </summary>
public sealed record MarketAlertPayload(
    string Text,
    string Severity);
