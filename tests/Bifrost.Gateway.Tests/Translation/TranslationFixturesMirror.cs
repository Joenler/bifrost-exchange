using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Contracts.Internal.Shared;
using Bifrost.Gateway.Translation;
using AuctionProto = Bifrost.Contracts.Auction;
using EventsProto = Bifrost.Contracts.Events;
using MarketProto = Bifrost.Contracts.Market;
using RoundProto = Bifrost.Contracts.Round;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Translation;

/// <summary>
/// In-process mirror of the relevant builders from
/// <c>tests/Bifrost.Contracts.Translation.Tests/TranslationFixtures.cs</c>. This
/// avoids the cross-test-project reference problem described in the plan body
/// (referencing one Exe-style xUnit project from another causes MSBuild
/// conflicts). The body shape is identical so the production translator's
/// byte-equivalence assertions hold against both this mirror and the original
/// fixtures.
///
/// Each row produces (proto, dto) pairs that match TranslationFixtures.cs's
/// canonical fully-populated rows used in the existing CONT-07 round-trip tests.
/// </summary>
internal static class TranslationFixturesMirror
{
    // Canonical instrument / product
    public const string CanonicalInstrumentId = "DE.Hour.2026-04-23T10:00";
    public const string CanonicalDeliveryArea = "DE";
    public const long CanonicalStartNs = 1_745_400_000_000_000_000L;
    public const long CanonicalEndNs = 1_745_403_600_000_000_000L;
    public const MarketProto.ProductType CanonicalProductType = MarketProto.ProductType.Hour;

    // Canonical client / clock
    public const string CanonicalClientId = "team-alpha-1";
    public const long CanonicalTimestampNs = 1_745_400_000_000_000_107L;

    public static MarketProto.Instrument InstrumentProto() => new()
    {
        InstrumentId = CanonicalInstrumentId,
        DeliveryArea = CanonicalDeliveryArea,
        DeliveryPeriodStartNs = CanonicalStartNs,
        DeliveryPeriodEndNs = CanonicalEndNs,
        ProductType = CanonicalProductType,
    };

    public static InstrumentIdDto InstrumentDto() => new(
        DeliveryArea: CanonicalDeliveryArea,
        DeliveryPeriodStart: DateTimeOffset.FromUnixTimeMilliseconds(CanonicalStartNs / 1_000_000L),
        DeliveryPeriodEnd: DateTimeOffset.FromUnixTimeMilliseconds(CanonicalEndNs / 1_000_000L));

    // ========================================================================
    // Row 1: OrderSubmit
    // ========================================================================

    public static StrategyProto.OrderSubmit OrderSubmitProto() => new()
    {
        ClientId = CanonicalClientId,
        Instrument = InstrumentProto(),
        Side = MarketProto.Side.Buy,
        OrderType = MarketProto.OrderType.Limit,
        PriceTicks = 42_000_000L,
        QuantityTicks = 50_000L,
        DisplaySliceTicks = 10_000L,
        ClientOrderId = "co-12345",
    };

    public static SubmitOrderCommand OrderSubmitDto() => new(
        ClientId: CanonicalClientId,
        InstrumentId: InstrumentDto(),
        Side: "Buy",
        OrderType: "Limit",
        PriceTicks: 42_000_000L,
        Quantity: QuantityScale.FromTicks(50_000L),
        DisplaySliceSize: QuantityScale.FromTicks(10_000L));

    // ========================================================================
    // Row 2: OrderCancel
    // ========================================================================

    public static StrategyProto.OrderCancel OrderCancelProto() => new()
    {
        ClientId = CanonicalClientId,
        OrderId = 9_111_222L,
        Instrument = InstrumentProto(),
    };

    public static CancelOrderCommand OrderCancelDto() => new(
        ClientId: CanonicalClientId,
        OrderId: 9_111_222L,
        InstrumentId: InstrumentDto());

    // ========================================================================
    // Row 3: OrderReplace
    // ========================================================================

    public static StrategyProto.OrderReplace OrderReplaceProto() => new()
    {
        ClientId = CanonicalClientId,
        OrderId = 9_111_222L,
        NewPriceTicks = 41_000_000L,
        NewQuantityTicks = 60_000L,
        Instrument = InstrumentProto(),
    };

    public static ReplaceOrderCommand OrderReplaceDto() => new(
        ClientId: CanonicalClientId,
        OrderId: 9_111_222L,
        NewPriceTicks: 41_000_000L,
        NewQuantity: QuantityScale.FromTicks(60_000L),
        InstrumentId: InstrumentDto());

    // ========================================================================
    // Row 4: OrderAccepted (private)
    // ========================================================================

    public static OrderAcceptedEvent OrderAcceptedDto() => new(
        OrderId: 7_777_001L,
        ClientId: CanonicalClientId,
        InstrumentId: InstrumentDto(),
        Side: "Buy",
        OrderType: "Limit",
        PriceTicks: 42_000_000L,
        Quantity: QuantityScale.FromTicks(50_000L),
        DisplaySliceSize: QuantityScale.FromTicks(10_000L),
        TimestampNs: CanonicalTimestampNs);

    public static StrategyProto.OrderAck OrderAcceptedProto() => new()
    {
        ClientOrderId = "co-12345",
        OrderId = 7_777_001L,
        Instrument = InstrumentProto(),
    };

    // ========================================================================
    // Row 5: OrderRejected (private)
    // ========================================================================

    public static OrderRejectedEvent OrderRejectedDto() => new(
        OrderId: 999_111L,
        ClientId: CanonicalClientId,
        Reason: "MaxNotional",
        TimestampNs: CanonicalTimestampNs);

    public static StrategyProto.OrderReject OrderRejectedProto(string clientOrderId = "co-12345", string detail = "notional cap exceeded") => new()
    {
        ClientOrderId = clientOrderId,
        Reason = StrategyProto.RejectReason.MaxNotional,
        Detail = detail,
    };

    // ========================================================================
    // Row 6: OrderExecuted (private)
    // ========================================================================

    public static OrderExecutedEvent OrderExecutedDto() => new(
        TradeId: 2_001L,
        OrderId: 7_777_001L,
        ClientId: CanonicalClientId,
        InstrumentId: InstrumentDto(),
        PriceTicks: 42_000_000L,
        FilledQuantity: QuantityScale.FromTicks(20_000L),
        RemainingQuantity: QuantityScale.FromTicks(30_000L),
        Side: "Buy",
        IsAggressor: true,
        Fee: QuantityScale.FromTicks(50L),
        TimestampNs: CanonicalTimestampNs);

    public static StrategyProto.Fill OrderExecutedProto() => new()
    {
        ClientId = CanonicalClientId,
        Instrument = InstrumentProto(),
        OrderId = 7_777_001L,
        TradeId = 2_001L,
        PriceTicks = 42_000_000L,
        FilledQuantityTicks = 20_000L,
        RemainingQuantityTicks = 30_000L,
        Side = MarketProto.Side.Buy,
        IsAggressor = true,
        FeeTicks = 50L,
    };

    // ========================================================================
    // Row 6b: OrderCancelled (private)
    // ========================================================================

    public static OrderCancelledEvent OrderCancelledDto() => new(
        OrderId: 7_777_001L,
        ClientId: CanonicalClientId,
        InstrumentId: InstrumentDto(),
        RemainingQuantity: QuantityScale.FromTicks(30_000L),
        TimestampNs: CanonicalTimestampNs);

    // ========================================================================
    // Row 7: BookDelta (public)
    // ========================================================================

    public static BookDeltaEvent BookDeltaDto() => new(
        InstrumentId: InstrumentDto(),
        Sequence: 12_345L,
        ChangedBids: new[]
        {
            new BookLevelDto(PriceTicks: 41_900_000L, Quantity: QuantityScale.FromTicks(20_000L), OrderCount: 2),
            new BookLevelDto(PriceTicks: 41_800_000L, Quantity: QuantityScale.FromTicks(15_000L), OrderCount: 1),
        },
        ChangedAsks: new[]
        {
            new BookLevelDto(PriceTicks: 42_100_000L, Quantity: QuantityScale.FromTicks(25_000L), OrderCount: 3),
        },
        TimestampNs: CanonicalTimestampNs);

    public static StrategyProto.BookUpdate BookDeltaProto()
    {
        var bu = new StrategyProto.BookUpdate
        {
            Instrument = InstrumentProto(),
            Book = new MarketProto.BookView
            {
                Sequence = 12_345L,
                TimestampNs = CanonicalTimestampNs,
            },
        };
        bu.Book.Bids.Add(new MarketProto.BookLevel { PriceTicks = 41_900_000L, QuantityTicks = 20_000L, OrderCount = 2 });
        bu.Book.Bids.Add(new MarketProto.BookLevel { PriceTicks = 41_800_000L, QuantityTicks = 15_000L, OrderCount = 1 });
        bu.Book.Asks.Add(new MarketProto.BookLevel { PriceTicks = 42_100_000L, QuantityTicks = 25_000L, OrderCount = 3 });
        return bu;
    }

    // ========================================================================
    // Row 8: PublicTrade (public)
    // ========================================================================

    public static PublicTradeEvent PublicTradeDto() => new(
        TradeId: 5_500L,
        InstrumentId: InstrumentDto(),
        PriceTicks: 42_050_000L,
        Quantity: QuantityScale.FromTicks(8_000L),
        AggressorSide: "Sell",
        TickSize: 100L,
        Sequence: 12_346L,
        TimestampNs: CanonicalTimestampNs);

    public static StrategyProto.Trade PublicTradeProto() => new()
    {
        Instrument = InstrumentProto(),
        TradeId = 5_500L,
        PriceTicks = 42_050_000L,
        QuantityTicks = 8_000L,
        AggressorSide = MarketProto.Side.Sell,
        Sequence = 12_346L,
    };

    // ========================================================================
    // Row 9: ForecastUpdate (public)
    // ========================================================================

    public static ForecastUpdateEvent ForecastUpdateDto() => new(
        ForecastPriceTicks: 42_500_000L,
        HorizonNs: 600_000_000_000L,
        TimestampNs: CanonicalTimestampNs);

    public static StrategyProto.ForecastUpdate ForecastUpdateProto() => new()
    {
        ForecastPriceTicks = 42_500_000L,
        HorizonNs = 600_000_000_000L,
    };

    // ========================================================================
    // Row 10: ForecastRevision (public)
    // ========================================================================

    public static ForecastRevisionEvent ForecastRevisionDto() => new(
        NewForecastPriceTicks: 41_900_000L,
        Reason: "mc_revise",
        TimestampNs: CanonicalTimestampNs);

    public static EventsProto.ForecastRevision ForecastRevisionProto() => new()
    {
        NewForecastPriceTicks = 41_900_000L,
        Reason = "mc_revise",
    };

    // ========================================================================
    // RegimeChange (public; Phase 03 — DTO is JSON-shaped { from, to, mcForced })
    // ========================================================================

    public static EventsProto.RegimeChange RegimeChangeProto() => new()
    {
        From = EventsProto.Regime.Calm,
        To = EventsProto.Regime.Volatile,
        McForced = true,
    };

    public static JsonElement RegimeChangeJson()
    {
        var obj = new { from = "Calm", to = "Volatile", mcForced = true };
        var json = JsonSerializer.Serialize(obj, JsonOpts);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // ========================================================================
    // Row 11: PhysicalShock (public)
    // ========================================================================

    public static PhysicalShockEvent PhysicalShockDto() => new(
        Mw: -1_500,
        Label: "generator_trip_brokdorf",
        Persistence: "Round",
        QuarterIndex: 2,
        TimestampNs: CanonicalTimestampNs);

    public static EventsProto.PhysicalShock PhysicalShockProto() => new()
    {
        Mw = -1_500,
        Label = "generator_trip_brokdorf",
        Persistence = EventsProto.ShockPersistence.Round,
        QuarterIndex = 2,
    };

    // ========================================================================
    // Row 12: ImbalancePrint (public)
    // ========================================================================

    public static ImbalancePrintEvent ImbalancePrintDto() => new(
        RoundNumber: 3,
        InstrumentId: InstrumentDto(),
        QuarterIndex: 1,
        PImbTicks: 5_500_000L,
        ATotalTicks: -2_400_000L,
        APhysicalTicks: -800_000L,
        Regime: "Trending",
        TimestampNs: CanonicalTimestampNs);

    public static MarketProto.ImbalancePrint ImbalancePrintProto() => new()
    {
        RoundNumber = 3,
        Instrument = InstrumentProto(),
        QuarterIndex = 1,
        PImbTicks = 5_500_000L,
        ATotalTicks = -2_400_000L,
        APhysicalTicks = -800_000L,
        Regime = EventsProto.Regime.Trending,
        TimestampNs = CanonicalTimestampNs,
    };

    // ========================================================================
    // ImbalanceSettlement (private; no gRPC oneof)
    // ========================================================================

    public static ImbalanceSettlementEvent ImbalanceSettlementDto() => new(
        RoundNumber: 3,
        ClientId: CanonicalClientId,
        InstrumentId: InstrumentDto(),
        QuarterIndex: 2,
        PositionTicks: 12_500L,
        PImbTicks: 5_500_000L,
        ImbalancePnlTicks: 12_500L * 5_500_000L,
        TimestampNs: CanonicalTimestampNs);

    // ========================================================================
    // RoundState (Phase 06)
    // ========================================================================

    public static RoundStateChangedPayload RoundStateDto() => new(
        State: "RoundOpen",
        RoundNumber: 4,
        ScenarioSeedOnWire: 0L,
        TransitionNs: 1_745_400_000_000_000_000L,
        ExpectedNextTransitionNs: 1_745_400_600_000_000_000L,
        Paused: false,
        PausedReason: null,
        Blocked: false,
        BlockedReason: null,
        IsReconciliation: false,
        IterationSeedRotationCount: 0,
        AbortReason: null);

    public static RoundProto.RoundState RoundStateProto() => new()
    {
        State = RoundProto.State.RoundOpen,
        RoundNumber = 4,
        ScenarioSeed = 0L,
        TransitionNs = 1_745_400_000_000_000_000L,
        ExpectedNextTransitionNs = 1_745_400_600_000_000_000L,
    };

    // ========================================================================
    // AuctionClearingResult (Phase 05)
    // ========================================================================

    public static ClearingResultDto AuctionClearingResultDto() => new(
        QuarterId: "Q1",
        ClearingPriceTicks: 41_500_000L,
        AwardedQuantityTicks: 25_000L,
        TeamName: "alpha");

    // ========================================================================
    // BidMatrixSubmit
    // ========================================================================

    public static StrategyProto.BidMatrixSubmit BidMatrixSubmitProto()
    {
        var matrix = new AuctionProto.BidMatrix
        {
            TeamName = "alpha-from-client", // ignored on ingress; gateway resolves
            QuarterId = "Q1",
        };
        matrix.BuySteps.Add(new AuctionProto.BidStep { PriceTicks = 41_000_000L, QuantityTicks = 10_000L });
        matrix.BuySteps.Add(new AuctionProto.BidStep { PriceTicks = 40_500_000L, QuantityTicks = 15_000L });
        matrix.SellSteps.Add(new AuctionProto.BidStep { PriceTicks = 42_500_000L, QuantityTicks = 8_000L });
        return new StrategyProto.BidMatrixSubmit { Matrix = matrix };
    }

    public static BidMatrixDto BidMatrixDtoExpected(string teamName) => new(
        TeamName: teamName,
        QuarterId: "Q1",
        BuySteps: new[]
        {
            new BidStepDto(PriceTicks: 41_000_000L, QuantityTicks: 10_000L),
            new BidStepDto(PriceTicks: 40_500_000L, QuantityTicks: 15_000L),
        },
        SellSteps: new[]
        {
            new BidStepDto(PriceTicks: 42_500_000L, QuantityTicks: 8_000L),
        });

    // ========================================================================
    // Helper: wrap an envelope around a DTO using the production JSON shape.
    // ========================================================================

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Envelope<JsonElement> WrapEnvelope<T>(string messageType, T dto, long sequence = 1L, long timestampMs = 0L)
        where T : notnull
    {
        var ts = timestampMs == 0L
            ? DateTimeOffset.FromUnixTimeMilliseconds(CanonicalTimestampNs / 1_000_000L)
            : DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        var element = JsonSerializer.SerializeToElement(dto, JsonOpts);
        return new Envelope<JsonElement>(
            MessageType: messageType,
            TimestampUtc: ts,
            CorrelationId: "corr-1",
            ClientId: CanonicalClientId,
            InstrumentId: CanonicalInstrumentId,
            Sequence: sequence,
            Payload: element);
    }

    public static Envelope<JsonElement> WrapEnvelopeRaw(string messageType, JsonElement payload, long sequence = 1L)
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(CanonicalTimestampNs / 1_000_000L);
        return new Envelope<JsonElement>(
            MessageType: messageType,
            TimestampUtc: ts,
            CorrelationId: "corr-1",
            ClientId: CanonicalClientId,
            InstrumentId: CanonicalInstrumentId,
            Sequence: sequence,
            Payload: payload);
    }

    public static OutboundTranslator.OutboundContext FullContext(
        string clientOrderId = "co-12345",
        string detail = "notional cap exceeded") =>
        new(
            InstrumentId: CanonicalInstrumentId,
            ProductType: CanonicalProductType,
            ClientOrderId: clientOrderId,
            Detail: detail,
            TickSize: 100L);
}
