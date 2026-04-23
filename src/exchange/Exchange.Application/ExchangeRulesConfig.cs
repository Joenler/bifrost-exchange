namespace Bifrost.Exchange.Application;

public sealed record ExchangeRulesConfig(
    long TickSize,
    decimal MinQuantity,
    decimal QuantityStep,
    decimal MakerFeeRate,
    decimal TakerFeeRate,
    long PriceScale = 10);
