using Google.Protobuf;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Contracts.Internal.Shared;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;
// NOTE: deliberately NOT importing Bifrost.Contracts.Internal.Events at file scope.
// A JSON-only long?-as-string helper lives in that namespace; Pitfall A (RESEARCH §12)
// requires it stay absent from the translation layer because gRPC carries int64
// timestamp_ns directly (not JSON-wrapped). DTO event types are fully-qualified below.

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 test-only translation layer for the 8-row proto ↔ RabbitMQ-DTO matrix.
///
/// Phase 07 Gateway will own the real production translator; Phase 01 ships this
/// test-scaffolding layer as proof the mapping is lossless. Every inbound command
/// (3 rows: OrderSubmit / OrderCancel / OrderReplace) and every outbound event
/// with an Arena DTO counterpart (5 rows: OrderAck / OrderReject / Fill /
/// BookUpdate / Trade) has a ToInternal(proto) and ToProto(dto, …) overload here.
///
/// Excluded by design per docs/gateway-mapping.md:
///   • Register          — gateway-terminal control-plane command; no RabbitMQ DTO.
///   • BidMatrixSubmit   — HTTP-JSON auction path; does not cross the gRPC↔DTO
///                          RabbitMQ boundary this plan guards.
///
/// Skipped in Phase 01 per RESEARCH §3.5 (BIFROST-specific; companion DTOs land
/// in later phases): ForecastUpdate / RoundState / Scorecard / PositionSnapshot
/// and events.proto oneof variants.
///
/// Boundary rules (D-01) applied here:
///   • Side enum ↔ "Buy" / "Sell" string
///   • OrderType enum ↔ "Limit" / "Market" / "Iceberg" / "FillOrKill" string
///   • RejectReason enum ↔ "Structural" / "RateLimited" / … string
///   • int64 *_ticks (quantity scale) ↔ decimal via <see cref="QuantityScale"/>
///   • int64 price_ticks with Phase-01 null-semantics: 0 ↔ null
///   • Instrument message ↔ InstrumentIdDto with ns ↔ DateTimeOffset (lossless at
///     whole-millisecond boundaries; fixtures use .000 ns values)
///
/// Proto-only fields (no DTO counterpart) are passed back through an extra
/// parameter on the ToProto(...) overload so bit-equivalence round-trip holds.
/// Phase 07 production translator will carry these via envelope/metadata fields;
/// for the CONT-07 gate the extra-parameter shape is sufficient.
/// </summary>
internal static class TranslationFixtures
{
    // ========================================================================
    // Shared: Instrument / InstrumentIdDto
    // ========================================================================
    //
    // Proto Instrument carries instrument_id + product_type that InstrumentIdDto
    // does not; these are recovered by passing the originals into ToProto(...).

    public static InstrumentIdDto ToInternal(MarketProto.Instrument p) => new(
        DeliveryArea: p.DeliveryArea,
        DeliveryPeriodStart: DateTimeOffset.FromUnixTimeMilliseconds(p.DeliveryPeriodStartNs / 1_000_000L),
        DeliveryPeriodEnd: DateTimeOffset.FromUnixTimeMilliseconds(p.DeliveryPeriodEndNs / 1_000_000L));

    public static MarketProto.Instrument ToProto(
        InstrumentIdDto d,
        string instrumentId,
        MarketProto.ProductType productType) => new()
    {
        InstrumentId = instrumentId,
        DeliveryArea = d.DeliveryArea,
        DeliveryPeriodStartNs = d.DeliveryPeriodStart.ToUnixTimeMilliseconds() * 1_000_000L,
        DeliveryPeriodEndNs = d.DeliveryPeriodEnd.ToUnixTimeMilliseconds() * 1_000_000L,
        ProductType = productType,
    };

    // ========================================================================
    // Shared: BookLevel / BookLevelDto
    // ========================================================================

    public static Bifrost.Contracts.Internal.Events.BookLevelDto ToInternal(MarketProto.BookLevel p) => new(
        PriceTicks: p.PriceTicks,
        Quantity: QuantityScale.FromTicks(p.QuantityTicks),
        OrderCount: p.OrderCount);

    public static MarketProto.BookLevel ToProto(Bifrost.Contracts.Internal.Events.BookLevelDto d) => new()
    {
        PriceTicks = d.PriceTicks,
        QuantityTicks = QuantityScale.ToTicks(d.Quantity),
        OrderCount = d.OrderCount,
    };

    // ========================================================================
    // Row 1: OrderSubmit ↔ SubmitOrderCommand
    // ========================================================================
    //
    // Proto-only field: client_order_id (team-supplied correlation key).

    public static SubmitOrderCommand ToInternal(StrategyProto.OrderSubmit p) => new(
        ClientId: p.ClientId,
        InstrumentId: ToInternal(p.Instrument),
        Side: SideEnumToString(p.Side),
        OrderType: OrderTypeEnumToString(p.OrderType),
        PriceTicks: p.PriceTicks == 0 ? null : p.PriceTicks,
        Quantity: QuantityScale.FromTicks(p.QuantityTicks),
        DisplaySliceSize: p.DisplaySliceTicks == 0 ? null : QuantityScale.FromTicks(p.DisplaySliceTicks));

    public static StrategyProto.OrderSubmit ToProto(
        SubmitOrderCommand d,
        string clientOrderId,
        string instrumentId,
        MarketProto.ProductType productType) => new()
    {
        ClientId = d.ClientId,
        Instrument = ToProto(d.InstrumentId, instrumentId, productType),
        Side = SideStringToEnum(d.Side),
        OrderType = OrderTypeStringToEnum(d.OrderType),
        PriceTicks = d.PriceTicks ?? 0,
        QuantityTicks = QuantityScale.ToTicks(d.Quantity),
        DisplaySliceTicks = d.DisplaySliceSize.HasValue ? QuantityScale.ToTicks(d.DisplaySliceSize.Value) : 0,
        ClientOrderId = clientOrderId,
    };

    // ========================================================================
    // Row 2: OrderCancel ↔ CancelOrderCommand
    // ========================================================================

    public static CancelOrderCommand ToInternal(StrategyProto.OrderCancel p) => new(
        ClientId: p.ClientId,
        OrderId: p.OrderId,
        InstrumentId: ToInternal(p.Instrument));

    public static StrategyProto.OrderCancel ToProto(
        CancelOrderCommand d,
        string instrumentId,
        MarketProto.ProductType productType) => new()
    {
        ClientId = d.ClientId,
        OrderId = d.OrderId,
        Instrument = ToProto(d.InstrumentId, instrumentId, productType),
    };

    // ========================================================================
    // Row 3: OrderReplace ↔ ReplaceOrderCommand
    // ========================================================================

    public static ReplaceOrderCommand ToInternal(StrategyProto.OrderReplace p) => new(
        ClientId: p.ClientId,
        OrderId: p.OrderId,
        NewPriceTicks: p.NewPriceTicks == 0 ? null : p.NewPriceTicks,
        NewQuantity: p.NewQuantityTicks == 0 ? null : QuantityScale.FromTicks(p.NewQuantityTicks),
        InstrumentId: ToInternal(p.Instrument));

    public static StrategyProto.OrderReplace ToProto(
        ReplaceOrderCommand d,
        string instrumentId,
        MarketProto.ProductType productType) => new()
    {
        ClientId = d.ClientId,
        OrderId = d.OrderId,
        NewPriceTicks = d.NewPriceTicks ?? 0,
        NewQuantityTicks = d.NewQuantity.HasValue ? QuantityScale.ToTicks(d.NewQuantity.Value) : 0,
        Instrument = ToProto(d.InstrumentId, instrumentId, productType),
    };

    // ========================================================================
    // Row 4: OrderAck ↔ OrderAcceptedEvent
    // ========================================================================
    //
    // OrderAck proto carries only (client_order_id, order_id, instrument); the
    // OrderAcceptedEvent DTO carries the full replay/accept fingerprint (client_id,
    // side, order_type, price_ticks, quantity, display_slice_size, timestamp_ns).
    //
    // Phase 07 will populate DTO-only fields from the originating SubmitOrderCommand
    // and envelope. For the CONT-07 test we carry them through the ToProto signature
    // as "state we would have known" so byte-equivalence holds on the wire-shaped
    // OrderAck (the 3 proto-present fields on the wire all survive).

    public static Bifrost.Contracts.Internal.Events.OrderAcceptedEvent ToInternal(
        StrategyProto.OrderAck p,
        string clientId,
        MarketProto.Side side,
        MarketProto.OrderType orderType,
        long? priceTicks,
        decimal quantity,
        decimal? displaySliceSize,
        long timestampNs) => new(
        OrderId: p.OrderId,
        ClientId: clientId,
        InstrumentId: ToInternal(p.Instrument),
        Side: SideEnumToString(side),
        OrderType: OrderTypeEnumToString(orderType),
        PriceTicks: priceTicks,
        Quantity: quantity,
        DisplaySliceSize: displaySliceSize,
        TimestampNs: timestampNs);

    public static StrategyProto.OrderAck ToProto(
        Bifrost.Contracts.Internal.Events.OrderAcceptedEvent d,
        string clientOrderId,
        string instrumentId,
        MarketProto.ProductType productType) => new()
    {
        ClientOrderId = clientOrderId,
        OrderId = d.OrderId,
        Instrument = ToProto(d.InstrumentId, instrumentId, productType),
    };

    // ========================================================================
    // Row 5: OrderReject ↔ OrderRejectedEvent
    // ========================================================================
    //
    // Proto OrderReject: (client_order_id, reason, detail) — no client_id /
    // order_id on the wire. DTO OrderRejectedEvent: (order_id, client_id, reason,
    // timestamp_ns) — no client_order_id / detail.
    //
    // Overlap is the Reason enum↔string. client_order_id and detail ride as extra
    // ToProto parameters; client_id, order_id, timestamp_ns ride as extra
    // ToInternal parameters.

    public static Bifrost.Contracts.Internal.Events.OrderRejectedEvent ToInternal(
        StrategyProto.OrderReject p,
        long orderId,
        string clientId,
        long timestampNs) => new(
        OrderId: orderId,
        ClientId: clientId,
        Reason: RejectReasonEnumToString(p.Reason),
        TimestampNs: timestampNs);

    public static StrategyProto.OrderReject ToProto(
        Bifrost.Contracts.Internal.Events.OrderRejectedEvent d,
        string clientOrderId,
        string detail) => new()
    {
        ClientOrderId = clientOrderId,
        Reason = RejectReasonStringToEnum(d.Reason),
        Detail = detail,
    };

    // ========================================================================
    // Row 6: Fill ↔ OrderExecutedEvent
    // ========================================================================
    //
    // DTO carries timestamp_ns; proto Fill does not (it lives on the enclosing
    // MarketEvent envelope). We pass timestamp_ns through ToInternal.

    public static Bifrost.Contracts.Internal.Events.OrderExecutedEvent ToInternal(
        StrategyProto.Fill p,
        long timestampNs) => new(
        TradeId: p.TradeId,
        OrderId: p.OrderId,
        ClientId: p.ClientId,
        InstrumentId: ToInternal(p.Instrument),
        PriceTicks: p.PriceTicks,
        FilledQuantity: QuantityScale.FromTicks(p.FilledQuantityTicks),
        RemainingQuantity: QuantityScale.FromTicks(p.RemainingQuantityTicks),
        Side: SideEnumToString(p.Side),
        IsAggressor: p.IsAggressor,
        Fee: QuantityScale.FromTicks(p.FeeTicks),
        TimestampNs: timestampNs);

    public static StrategyProto.Fill ToProto(
        Bifrost.Contracts.Internal.Events.OrderExecutedEvent d,
        string instrumentId,
        MarketProto.ProductType productType) => new()
    {
        ClientId = d.ClientId,
        Instrument = ToProto(d.InstrumentId, instrumentId, productType),
        OrderId = d.OrderId,
        TradeId = d.TradeId,
        PriceTicks = d.PriceTicks,
        FilledQuantityTicks = QuantityScale.ToTicks(d.FilledQuantity),
        RemainingQuantityTicks = QuantityScale.ToTicks(d.RemainingQuantity),
        Side = SideStringToEnum(d.Side),
        IsAggressor = d.IsAggressor,
        FeeTicks = QuantityScale.ToTicks(d.Fee),
    };

    // ========================================================================
    // Row 7: BookUpdate ↔ BookDeltaEvent
    // ========================================================================
    //
    // Proto BookUpdate wraps (Instrument, BookView). BookView carries bids/asks
    // (full snapshots at v1) plus sequence + timestamp_ns. DTO BookDeltaEvent
    // flattens to (InstrumentId, Sequence, ChangedBids[], ChangedAsks[],
    // TimestampNs). The "changed" naming in the DTO is a delta label — at v1
    // Phase 01 it represents the full current levels, matching the BookView body.

    public static Bifrost.Contracts.Internal.Events.BookDeltaEvent ToInternal(StrategyProto.BookUpdate p) => new(
        InstrumentId: ToInternal(p.Instrument),
        Sequence: p.Book.Sequence,
        ChangedBids: p.Book.Bids.Select(ToInternal).ToArray(),
        ChangedAsks: p.Book.Asks.Select(ToInternal).ToArray(),
        TimestampNs: p.Book.TimestampNs);

    public static StrategyProto.BookUpdate ToProto(
        Bifrost.Contracts.Internal.Events.BookDeltaEvent d,
        string instrumentId,
        MarketProto.ProductType productType)
    {
        var bu = new StrategyProto.BookUpdate
        {
            Instrument = ToProto(d.InstrumentId, instrumentId, productType),
            Book = new MarketProto.BookView
            {
                Sequence = d.Sequence,
                TimestampNs = d.TimestampNs,
            },
        };
        foreach (var lvl in d.ChangedBids)
        {
            bu.Book.Bids.Add(ToProto(lvl));
        }
        foreach (var lvl in d.ChangedAsks)
        {
            bu.Book.Asks.Add(ToProto(lvl));
        }
        return bu;
    }

    // ========================================================================
    // Row 8: Trade ↔ PublicTradeEvent
    // ========================================================================
    //
    // DTO carries TickSize and TimestampNs; proto Trade carries neither (tick size
    // is exchange-config, timestamp rides on MarketEvent envelope). Pass them
    // through ToInternal.

    public static Bifrost.Contracts.Internal.Events.PublicTradeEvent ToInternal(
        StrategyProto.Trade p,
        long tickSize,
        long timestampNs) => new(
        TradeId: p.TradeId,
        InstrumentId: ToInternal(p.Instrument),
        PriceTicks: p.PriceTicks,
        Quantity: QuantityScale.FromTicks(p.QuantityTicks),
        AggressorSide: SideEnumToString(p.AggressorSide),
        TickSize: tickSize,
        Sequence: p.Sequence,
        TimestampNs: timestampNs);

    public static StrategyProto.Trade ToProto(
        Bifrost.Contracts.Internal.Events.PublicTradeEvent d,
        string instrumentId,
        MarketProto.ProductType productType) => new()
    {
        Instrument = ToProto(d.InstrumentId, instrumentId, productType),
        TradeId = d.TradeId,
        PriceTicks = d.PriceTicks,
        QuantityTicks = QuantityScale.ToTicks(d.Quantity),
        AggressorSide = SideStringToEnum(d.AggressorSide),
        Sequence = d.Sequence,
    };

    // ========================================================================
    // Enum ↔ string helpers (D-01 boundary rule)
    // ========================================================================

    public static string SideEnumToString(MarketProto.Side s) => s switch
    {
        MarketProto.Side.Buy => "Buy",
        MarketProto.Side.Sell => "Sell",
        _ => throw new ArgumentException($"Unknown side: {s}"),
    };

    public static MarketProto.Side SideStringToEnum(string s) => s switch
    {
        "Buy" => MarketProto.Side.Buy,
        "Sell" => MarketProto.Side.Sell,
        _ => throw new ArgumentException($"Unknown side: {s}"),
    };

    public static string OrderTypeEnumToString(MarketProto.OrderType t) => t switch
    {
        MarketProto.OrderType.Limit => "Limit",
        MarketProto.OrderType.Market => "Market",
        MarketProto.OrderType.Iceberg => "Iceberg",
        MarketProto.OrderType.Fok => "FillOrKill",
        _ => throw new ArgumentException($"Unknown order type: {t}"),
    };

    public static MarketProto.OrderType OrderTypeStringToEnum(string s) => s switch
    {
        "Limit" => MarketProto.OrderType.Limit,
        "Market" => MarketProto.OrderType.Market,
        "Iceberg" => MarketProto.OrderType.Iceberg,
        "FillOrKill" => MarketProto.OrderType.Fok,
        _ => throw new ArgumentException($"Unknown order type: {s}"),
    };

    public static string RejectReasonEnumToString(StrategyProto.RejectReason r) => r switch
    {
        StrategyProto.RejectReason.Structural => "Structural",
        StrategyProto.RejectReason.RateLimited => "RateLimited",
        StrategyProto.RejectReason.MaxOpenOrders => "MaxOpenOrders",
        StrategyProto.RejectReason.MaxNotional => "MaxNotional",
        StrategyProto.RejectReason.MaxPosition => "MaxPosition",
        StrategyProto.RejectReason.SelfTrade => "SelfTrade",
        StrategyProto.RejectReason.ExchangeClosed => "ExchangeClosed",
        StrategyProto.RejectReason.InsufficientLiquidity => "InsufficientLiquidity",
        StrategyProto.RejectReason.OrderNotFound => "OrderNotFound",
        StrategyProto.RejectReason.InvalidReplace => "InvalidReplace",
        StrategyProto.RejectReason.UnknownInstrument => "UnknownInstrument",
        StrategyProto.RejectReason.ReregisterRequired => "ReregisterRequired",
        _ => throw new ArgumentException($"Unknown reject reason: {r}"),
    };

    public static StrategyProto.RejectReason RejectReasonStringToEnum(string s) => s switch
    {
        "Structural" => StrategyProto.RejectReason.Structural,
        "RateLimited" => StrategyProto.RejectReason.RateLimited,
        "MaxOpenOrders" => StrategyProto.RejectReason.MaxOpenOrders,
        "MaxNotional" => StrategyProto.RejectReason.MaxNotional,
        "MaxPosition" => StrategyProto.RejectReason.MaxPosition,
        "SelfTrade" => StrategyProto.RejectReason.SelfTrade,
        "ExchangeClosed" => StrategyProto.RejectReason.ExchangeClosed,
        "InsufficientLiquidity" => StrategyProto.RejectReason.InsufficientLiquidity,
        "OrderNotFound" => StrategyProto.RejectReason.OrderNotFound,
        "InvalidReplace" => StrategyProto.RejectReason.InvalidReplace,
        "UnknownInstrument" => StrategyProto.RejectReason.UnknownInstrument,
        "ReregisterRequired" => StrategyProto.RejectReason.ReregisterRequired,
        _ => throw new ArgumentException($"Unknown reject reason: {s}"),
    };
}
