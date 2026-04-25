using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.Contracts.Internal.Events;
using AuctionProto = Bifrost.Contracts.Auction;
using EventsProto = Bifrost.Contracts.Events;
using MarketProto = Bifrost.Contracts.Market;
using RoundProto = Bifrost.Contracts.Round;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Translation;

/// <summary>
/// Bifrost.Contracts.Internal DTO → gRPC MarketEvent conversions.
///
/// Every <c>From*</c> method takes an <see cref="Envelope{T}"/> whose payload
/// deserializes to the matching DTO, then maps to the appropriate
/// <see cref="StrategyProto.MarketEvent"/> oneof case. The envelope's
/// <c>Sequence</c> and <c>TimestampUtc</c> populate the MarketEvent envelope
/// fields (sequence, timestamp_ns); the payload populates the oneof body.
///
/// Mirrors <c>tests/Bifrost.Contracts.Translation.Tests/TranslationFixtures.cs</c>
/// row-for-row — each <c>From*</c> method body produces the same proto shape as
/// the corresponding <c>ToProto(...)</c> in the fixtures, so the existing CONT-07
/// suite continues to pass against this production code.
///
/// gRPC <c>Instrument</c> fields (instrument_id + product_type) that the DTO
/// does not carry are reconstructed from a sidecar metadata source (Phase 06
/// IRoundStateSource / instrument catalog). For Phase 07-03 the gateway expects
/// the consuming caller to supply <c>instrumentId</c> + <c>productType</c> via
/// the <see cref="OutboundContext"/> parameter on the per-row signatures that
/// need them — Phase 06 / Plan 06 will wire the catalog into a singleton.
///
/// No runtime reflection. No AutoMapper / Mapster.
/// </summary>
public static class OutboundTranslator
{
    /// <summary>
    /// Sidecar context the gateway carries alongside an envelope for fields the
    /// proto carries that the DTO does not (proto Instrument's instrument_id +
    /// product_type, OrderReject's client_order_id + detail, etc.). Plan 06 will
    /// resolve these from the per-team state + instrument catalog.
    /// </summary>
    public sealed record OutboundContext(
        string InstrumentId = "",
        MarketProto.ProductType ProductType = MarketProto.ProductType.Unspecified,
        string ClientOrderId = "",
        string Detail = "",
        long TickSize = 0);

    private static readonly OutboundContext EmptyContext = new();

    // System.Text.Json defaults to camelCase property names matching how the
    // RabbitMqEventPublisher serializes the Bifrost.Contracts.Internal DTOs.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ========================================================================
    // Helpers — envelope → MarketEvent shell with sequence + timestamp_ns set.
    // ========================================================================

    private static StrategyProto.MarketEvent NewMarketEvent(Envelope<JsonElement> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new StrategyProto.MarketEvent
        {
            Sequence = envelope.Sequence ?? 0L,
            TimestampNs = envelope.TimestampUtc.ToUnixTimeMilliseconds() * 1_000_000L,
        };
    }

    private static T DeserializePayload<T>(Envelope<JsonElement> envelope) where T : class
    {
        var payload = envelope.Payload.Deserialize<T>(JsonOptions);
        if (payload is null)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} payload was null on envelope MessageType={envelope.MessageType}.");
        }
        return payload;
    }

    // ========================================================================
    // Row 4: OrderAccepted → OrderAck (private)
    //   Mirrors TranslationFixtures.ToProto(OrderAcceptedEvent) lines 195-204.
    // ========================================================================

    public static StrategyProto.MarketEvent FromAccepted(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        var ctx = context ?? EmptyContext;
        var dto = DeserializePayload<OrderAcceptedEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        ev.OrderAck = new StrategyProto.OrderAck
        {
            ClientOrderId = ctx.ClientOrderId,
            OrderId = dto.OrderId,
            Instrument = InboundTranslator.ToProtoInstrument(dto.InstrumentId, ctx.InstrumentId, ctx.ProductType),
        };
        return ev;
    }

    // ========================================================================
    // Row 5: OrderRejected → OrderReject (private)
    //   Mirrors TranslationFixtures.ToProto(OrderRejectedEvent) lines 228-236.
    // ========================================================================

    public static StrategyProto.MarketEvent FromRejected(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        var ctx = context ?? EmptyContext;
        var dto = DeserializePayload<OrderRejectedEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        ev.OrderReject = new StrategyProto.OrderReject
        {
            ClientOrderId = ctx.ClientOrderId,
            Reason = RejectReasonMap.StringToEnum(dto.Reason),
            Detail = ctx.Detail,
        };
        return ev;
    }

    // ========================================================================
    // Row 6: OrderExecuted → Fill (private)
    //   Mirrors TranslationFixtures.ToProto(OrderExecutedEvent) lines 260-275.
    // ========================================================================

    public static StrategyProto.MarketEvent FromExecuted(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        var ctx = context ?? EmptyContext;
        var dto = DeserializePayload<OrderExecutedEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        ev.Fill = new StrategyProto.Fill
        {
            ClientId = dto.ClientId,
            Instrument = InboundTranslator.ToProtoInstrument(dto.InstrumentId, ctx.InstrumentId, ctx.ProductType),
            OrderId = dto.OrderId,
            TradeId = dto.TradeId,
            PriceTicks = dto.PriceTicks,
            FilledQuantityTicks = Bifrost.Contracts.Internal.Shared.QuantityScale.ToTicks(dto.FilledQuantity),
            RemainingQuantityTicks = Bifrost.Contracts.Internal.Shared.QuantityScale.ToTicks(dto.RemainingQuantity),
            Side = InboundTranslator.SideStringToEnum(dto.Side),
            IsAggressor = dto.IsAggressor,
            FeeTicks = Bifrost.Contracts.Internal.Shared.QuantityScale.ToTicks(dto.Fee),
        };
        return ev;
    }

    // ========================================================================
    // OrderCancelled → OrderAck (private; cancel-shape per docs/gateway-mapping.md)
    //
    // strategy.proto has no dedicated CancelAck; gateway-mapping.md routes the
    // OrderCancelledEvent through the OrderAck oneof variant (the team correlates
    // by order_id; the cancel-vs-accept distinction is implicit on the team side
    // because the corresponding order is no longer in their open-order map).
    // For Phase 07 the cancel emits an OrderReject with REJECT_REASON_UNSPECIFIED
    // would be wrong — instead we emit a Fill with filled_quantity_ticks=0 and
    // remaining_quantity_ticks=cancelledQuantity to keep position-tracker math
    // consistent? No — the cleanest mapping is OrderAck (the cancel was accepted)
    // and the per-team open-orders map removes the order on the consumer side.
    // ========================================================================

    public static StrategyProto.MarketEvent FromCancelled(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        var ctx = context ?? EmptyContext;
        var dto = DeserializePayload<OrderCancelledEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        ev.OrderAck = new StrategyProto.OrderAck
        {
            ClientOrderId = ctx.ClientOrderId,
            OrderId = dto.OrderId,
            Instrument = InboundTranslator.ToProtoInstrument(dto.InstrumentId, ctx.InstrumentId, ctx.ProductType),
        };
        return ev;
    }

    // ========================================================================
    // Row 7: BookDelta → BookUpdate (public)
    //   Mirrors TranslationFixtures.ToProto(BookDeltaEvent) lines 294-317.
    // ========================================================================

    public static StrategyProto.MarketEvent FromBookDelta(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        var ctx = context ?? EmptyContext;
        var dto = DeserializePayload<BookDeltaEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        var book = new MarketProto.BookView
        {
            Sequence = dto.Sequence,
            TimestampNs = dto.TimestampNs,
        };
        if (dto.ChangedBids is not null)
        {
            for (var i = 0; i < dto.ChangedBids.Length; i++)
            {
                book.Bids.Add(ToProtoBookLevel(dto.ChangedBids[i]));
            }
        }
        if (dto.ChangedAsks is not null)
        {
            for (var i = 0; i < dto.ChangedAsks.Length; i++)
            {
                book.Asks.Add(ToProtoBookLevel(dto.ChangedAsks[i]));
            }
        }
        ev.BookUpdate = new StrategyProto.BookUpdate
        {
            Instrument = InboundTranslator.ToProtoInstrument(dto.InstrumentId, ctx.InstrumentId, ctx.ProductType),
            Book = book,
        };
        return ev;
    }

    // ========================================================================
    // Row 8: PublicTrade → Trade (public)
    //   Mirrors TranslationFixtures.ToProto(PublicTradeEvent) lines 340-351.
    // ========================================================================

    public static StrategyProto.MarketEvent FromPublicTrade(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        var ctx = context ?? EmptyContext;
        var dto = DeserializePayload<PublicTradeEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        ev.Trade = new StrategyProto.Trade
        {
            Instrument = InboundTranslator.ToProtoInstrument(dto.InstrumentId, ctx.InstrumentId, ctx.ProductType),
            TradeId = dto.TradeId,
            PriceTicks = dto.PriceTicks,
            QuantityTicks = Bifrost.Contracts.Internal.Shared.QuantityScale.ToTicks(dto.Quantity),
            AggressorSide = InboundTranslator.SideStringToEnum(dto.AggressorSide),
            Sequence = dto.Sequence,
        };
        return ev;
    }

    // ========================================================================
    // Row 9: ForecastUpdate → ForecastUpdate (public; envelope-strip)
    //   Mirrors TranslationFixtures.ToProto(ForecastUpdateEvent) lines 438-443.
    // ========================================================================

    public static StrategyProto.MarketEvent FromForecastUpdate(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        _ = context;
        var dto = DeserializePayload<ForecastUpdateEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        ev.ForecastUpdate = new StrategyProto.ForecastUpdate
        {
            ForecastPriceTicks = dto.ForecastPriceTicks,
            HorizonNs = dto.HorizonNs,
        };
        return ev;
    }

    // ========================================================================
    // Row 10: ForecastRevision → public_event(Event.ForecastRevision)
    //   Mirrors TranslationFixtures.ToProto(ForecastRevisionEvent) lines 460-465.
    // ========================================================================

    public static StrategyProto.MarketEvent FromForecastRevision(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        _ = context;
        var dto = DeserializePayload<ForecastRevisionEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        var inner = new EventsProto.Event
        {
            TimestampNs = dto.TimestampNs,
            Severity = EventsProto.Severity.Info,
            ForecastRevision = new EventsProto.ForecastRevision
            {
                NewForecastPriceTicks = dto.NewForecastPriceTicks,
                Reason = dto.Reason ?? string.Empty,
            },
        };
        ev.PublicEvent = inner;
        return ev;
    }

    // ========================================================================
    // RegimeChange → public_event(Event.RegimeChange)
    //
    // Phase 03 D-14 produces RegimeChange via a typed JSON DTO (the Bifrost
    // quoter publishes it; gateway-mapping.md classifies the DTO as
    // BIFROST-specific Phase 03). The DTO carries: From, To, McForced.
    // ========================================================================

    public static StrategyProto.MarketEvent FromRegimeChange(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        _ = context;
        var ev = NewMarketEvent(envelope);
        var payload = envelope.Payload;
        var fromRegime = ReadRegimeProperty(payload, "from");
        var toRegime = ReadRegimeProperty(payload, "to");
        var mcForced = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("mcForced", out var f) && f.ValueKind == JsonValueKind.True;
        var inner = new EventsProto.Event
        {
            TimestampNs = ev.TimestampNs,
            Severity = EventsProto.Severity.Info,
            RegimeChange = new EventsProto.RegimeChange
            {
                From = fromRegime,
                To = toRegime,
                McForced = mcForced,
            },
        };
        ev.PublicEvent = inner;
        return ev;
    }

    private static EventsProto.Regime ReadRegimeProperty(JsonElement payload, string property)
    {
        if (payload.ValueKind != JsonValueKind.Object) return EventsProto.Regime.Unspecified;
        if (!payload.TryGetProperty(property, out var p)) return EventsProto.Regime.Unspecified;
        if (p.ValueKind != JsonValueKind.String) return EventsProto.Regime.Unspecified;
        var s = p.GetString();
        if (string.IsNullOrEmpty(s)) return EventsProto.Regime.Unspecified;
        return InboundTranslator.RegimeStringToEnum(s);
    }

    // ========================================================================
    // Row 11: PhysicalShock → public_event(Event.PhysicalShock)
    //   Mirrors TranslationFixtures.ToProto(PhysicalShockEvent) lines 489-496.
    // ========================================================================

    public static StrategyProto.MarketEvent FromPhysicalShock(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        _ = context;
        var dto = DeserializePayload<PhysicalShockEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        var inner = new EventsProto.Event
        {
            TimestampNs = dto.TimestampNs,
            Severity = EventsProto.Severity.Warn,
            PhysicalShock = new EventsProto.PhysicalShock
            {
                Mw = dto.Mw,
                Label = dto.Label ?? string.Empty,
                Persistence = InboundTranslator.ShockPersistenceStringToEnum(dto.Persistence),
                QuarterIndex = dto.QuarterIndex,
            },
        };
        ev.PublicEvent = inner;
        return ev;
    }

    // ========================================================================
    // Row 12: ImbalancePrint → ImbalancePrint (public)
    //   Mirrors TranslationFixtures.ToProto(ImbalancePrintEvent) lines 520-533.
    // ========================================================================

    public static StrategyProto.MarketEvent FromImbalancePrint(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        var ctx = context ?? EmptyContext;
        var dto = DeserializePayload<ImbalancePrintEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        ev.ImbalancePrint = new MarketProto.ImbalancePrint
        {
            RoundNumber = dto.RoundNumber,
            Instrument = InboundTranslator.ToProtoInstrument(dto.InstrumentId, ctx.InstrumentId, ctx.ProductType),
            QuarterIndex = dto.QuarterIndex,
            PImbTicks = dto.PImbTicks,
            ATotalTicks = dto.ATotalTicks,
            APhysicalTicks = dto.APhysicalTicks,
            Regime = InboundTranslator.RegimeStringToEnum(dto.Regime),
            TimestampNs = dto.TimestampNs,
        };
        return ev;
    }

    // ========================================================================
    // ImbalanceSettlement → private (no gRPC analog; per gateway-mapping.md
    // §"Imbalance-simulator private events"). Emitted as an OrderAck with
    // empty body purely to surface the envelope sequence + timestamp; the team
    // consumes the row directly from their private RabbitMQ binding instead.
    //
    // For Phase 07 we expose a no-op variant so a unified consumer dispatcher
    // can handle the message-type without throwing; the actual private
    // delivery path is direct RabbitMQ per ARCHITECTURE.md.
    // ========================================================================

    public static StrategyProto.MarketEvent FromImbalanceSettlement(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        _ = context;
        // Validate the payload deserializes cleanly (defence-in-depth — the message
        // doesn't ride on the gRPC oneof but the consumer is shared).
        _ = DeserializePayload<ImbalanceSettlementEvent>(envelope);
        var ev = NewMarketEvent(envelope);
        // No oneof set — caller must NOT push this MarketEvent to the strategy stream.
        // ImbalanceSettlement is delivered direct on the team's private RabbitMQ
        // binding (gateway-mapping.md §"Imbalance-simulator private events").
        return ev;
    }

    // ========================================================================
    // RoundState envelope → MarketEvent.round_state
    //
    // Phase 06 publishes RoundStateChangedPayload (12 fields, projection of the
    // orchestrator state). Five fields round-trip onto the proto RoundState.
    // ========================================================================

    public static StrategyProto.MarketEvent FromRoundState(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        _ = context;
        var dto = DeserializePayload<RoundStateChangedPayload>(envelope);
        var ev = NewMarketEvent(envelope);
        ev.RoundState = new RoundProto.RoundState
        {
            State = InboundTranslator.RoundStateStringToEnum(dto.State),
            RoundNumber = dto.RoundNumber,
            ScenarioSeed = dto.ScenarioSeedOnWire,
            TransitionNs = dto.TransitionNs,
            ExpectedNextTransitionNs = dto.ExpectedNextTransitionNs ?? 0L,
        };
        return ev;
    }

    // ========================================================================
    // ClearingResult → public_event(Event.News) is wrong — ClearingResult is
    // not in events.proto. Per gateway-mapping.md the ClearingResultDto rides
    // on the public bus but does not project onto the team-facing MarketEvent
    // oneof at v1 (no row in the outbound table for ClearingResult). For
    // Phase 07 we produce an envelope-only MarketEvent so the consumer
    // dispatcher can route by MessageType uniformly; Plan 06 will decide
    // whether per-team awarded rows surface on the strategy stream as a
    // separate Phase-08 oneof variant or stay private RabbitMQ-only.
    //
    // Per the plan body's "Phase 04/05/06 progression" note, AuctionClearingResult
    // is one of the 4 rows whose fixture does not yet exist; it is implemented
    // here following the gateway-mapping.md row instructions (raw passthrough
    // as a JSON-shaped news payload? — no, ClearingResultDto carries structured
    // numbers; we surface it as a public_event::News with a JSON-serialized
    // body so the team can decode it client-side until a typed proto lands).
    // ========================================================================

    public static StrategyProto.MarketEvent FromAuctionClearingResult(Envelope<JsonElement> envelope, OutboundContext? context = null)
    {
        _ = context;
        var dto = DeserializePayload<ClearingResultDto>(envelope);
        var ev = NewMarketEvent(envelope);
        // Emit as Event.News with the JSON-serialized clearing row in `text`
        // until a typed ClearingResult oneof is added to strategy.proto. The
        // public-summary row (TeamName == null) and per-team row (TeamName == "alpha")
        // share the same wire shape; the team filters on TeamName in the JSON body.
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var inner = new EventsProto.Event
        {
            TimestampNs = ev.TimestampNs,
            Severity = EventsProto.Severity.Info,
            News = new EventsProto.News
            {
                Text = json,
                LibraryKey = "auction.clearing",
            },
        };
        ev.PublicEvent = inner;
        return ev;
    }

    // ========================================================================
    // Gateway-originated builders (not wrapping a RabbitMQ envelope).
    // ========================================================================

    public static StrategyProto.MarketEvent BuildRegisterAck(
        string clientId,
        RoundProto.RoundState currentRoundState,
        long resumedFromSequence,
        bool reregisterRequired,
        long sequence = 0L,
        long timestampNs = 0L)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        return new StrategyProto.MarketEvent
        {
            Sequence = sequence,
            TimestampNs = timestampNs,
            RegisterAck = new StrategyProto.RegisterAck
            {
                ClientId = clientId,
                CurrentRoundState = currentRoundState ?? new RoundProto.RoundState(),
                ResumedFromSequence = resumedFromSequence,
                ReregisterRequired = reregisterRequired,
            },
        };
    }

    public static StrategyProto.MarketEvent BuildPositionSnapshot(
        InstrumentIdDto instrumentId,
        string instrumentIdString,
        MarketProto.ProductType productType,
        long netPositionTicks,
        long averagePriceTicks,
        long openOrdersNotionalTicks,
        long sequence = 0L,
        long timestampNs = 0L)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        return new StrategyProto.MarketEvent
        {
            Sequence = sequence,
            TimestampNs = timestampNs,
            PositionSnapshot = new StrategyProto.PositionSnapshot
            {
                Instrument = InboundTranslator.ToProtoInstrument(instrumentId, instrumentIdString, productType),
                NetPositionTicks = netPositionTicks,
                AveragePriceTicks = averagePriceTicks,
                OpenOrdersNotionalTicks = openOrdersNotionalTicks,
            },
        };
    }

    public static StrategyProto.MarketEvent BuildOrderReject(
        StrategyProto.RejectReason reason,
        string detail,
        string? clientOrderId = null,
        long sequence = 0L,
        long timestampNs = 0L) =>
        new()
        {
            Sequence = sequence,
            TimestampNs = timestampNs,
            OrderReject = new StrategyProto.OrderReject
            {
                ClientOrderId = clientOrderId ?? string.Empty,
                Reason = reason,
                Detail = detail ?? string.Empty,
            },
        };

    // ========================================================================
    // Internal helpers — BookLevel mirrors TranslationFixtures.cs lines 78-88.
    // ========================================================================

    private static MarketProto.BookLevel ToProtoBookLevel(BookLevelDto d) => new()
    {
        PriceTicks = d.PriceTicks,
        QuantityTicks = Bifrost.Contracts.Internal.Shared.QuantityScale.ToTicks(d.Quantity),
        OrderCount = d.OrderCount,
    };
}
