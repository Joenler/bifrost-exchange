namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Exchange broadcast of configuration metadata including tick sizes and fee rates.
/// </summary>
public sealed record ExchangeMetadataEvent(
    long TickSize,
    decimal MinQuantity,
    decimal QuantityStep,
    decimal MakerFeeRate,
    decimal TakerFeeRate,
    long PriceScale,
    long TimestampNs);
