using System.Text.Json.Serialization;

namespace Bifrost.Contracts.Internal.Events;

public sealed class OrderEventDto
{
    public string EventType { get; set; } = "";
    public string StrategyName { get; set; } = "";
    public string ClientId { get; set; } = "";
    public long TimestampNs { get; set; }
    public string? InstrumentId { get; set; }
    public long? OrderId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Side { get; set; }
    public long? PriceTicks { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? FilledQuantity { get; set; }
    public decimal? RemainingQuantity { get; set; }
    public string? Reason { get; set; }
    public long? TradeId { get; set; }
    public bool? IsAggressor { get; set; }
    public decimal? Fee { get; set; }

    [JsonConverter(typeof(NanosecondStringConverter))]
    public long? ExchangeTimestampNs { get; set; }

    [JsonConverter(typeof(NanosecondStringConverter))]
    public long? LocalTimestampNs { get; set; }

    public void Reset()
    {
        EventType = "";
        StrategyName = "";
        ClientId = "";
        TimestampNs = 0;
        InstrumentId = null;
        OrderId = null;
        CorrelationId = null;
        Side = null;
        PriceTicks = null;
        Quantity = null;
        FilledQuantity = null;
        RemainingQuantity = null;
        Reason = null;
        TradeId = null;
        IsAggressor = null;
        Fee = null;
        ExchangeTimestampNs = null;
        LocalTimestampNs = null;
    }
}
