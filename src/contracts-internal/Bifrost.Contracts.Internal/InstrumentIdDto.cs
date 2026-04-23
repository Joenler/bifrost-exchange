namespace Bifrost.Contracts.Internal;

/// <summary>
/// Wire-format instrument identity with area, delivery period, and granularity.
/// </summary>
public sealed record InstrumentIdDto(
    string DeliveryArea,
    DateTimeOffset DeliveryPeriodStart,
    DateTimeOffset DeliveryPeriodEnd);
