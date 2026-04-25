using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Contracts.Internal.Shared;
using AuctionProto = Bifrost.Contracts.Auction;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Translation;

/// <summary>
/// gRPC StrategyCommand → Bifrost.Contracts.Internal DTO conversions.
///
/// Mirrors <c>tests/Bifrost.Contracts.Translation.Tests/TranslationFixtures.cs</c>
/// row-for-row so the existing CONT-07 byte-equivalence suite continues to pass
/// against this production code. Each <c>ToInternal*</c> method has an
/// equivalent helper in TranslationFixtures.cs (Row 1 / Row 2 / Row 3 of
/// docs/gateway-mapping.md inbound section).
///
/// SPEC req 9 + Phase 03 quoter handoff: any ClientId equal to "quoter" or
/// "dah-auction" (case-insensitive) is rejected at the boundary BEFORE any DTO
/// is constructed. Callers (StrategyGatewayService) MUST check
/// <see cref="IsReservedClientId"/> first, and the per-row constructors below
/// also assert as a defence-in-depth check.
///
/// No runtime reflection. No AutoMapper / Mapster — see RESEARCH §Don't-Hand-Roll.
/// </summary>
public static class InboundTranslator
{
    private static readonly string[] Reserved = { "quoter", "dah-auction" };

    /// <summary>
    /// Returns true if the supplied client_id matches a reserved central-machine
    /// identity ("quoter", "dah-auction") under case-insensitive comparison.
    /// Translators throw on a reserved id; callers should reject with
    /// REJECT_REASON_STRUCTURAL before invoking any ToInternal* method.
    /// </summary>
    public static bool IsReservedClientId(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return false;
        for (var i = 0; i < Reserved.Length; i++)
        {
            if (string.Equals(Reserved[i], clientId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ========================================================================
    // Row 1: OrderSubmit ↔ SubmitOrderCommand
    //   Mirrors TranslationFixtures.ToInternal(OrderSubmit) lines 96-103.
    // ========================================================================

    public static SubmitOrderCommand ToInternalSubmit(StrategyProto.OrderSubmit p, string resolvedClientId)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedClientId);
        if (IsReservedClientId(resolvedClientId))
        {
            throw new InvalidOperationException(
                $"Reserved ClientId '{resolvedClientId}' — caller must reject with REJECT_REASON_STRUCTURAL before invoking translator.");
        }
        return new SubmitOrderCommand(
            ClientId: resolvedClientId,
            InstrumentId: ToInternalInstrument(p.Instrument),
            Side: SideEnumToString(p.Side),
            OrderType: OrderTypeEnumToString(p.OrderType),
            PriceTicks: p.PriceTicks == 0 ? null : p.PriceTicks,
            Quantity: QuantityScale.FromTicks(p.QuantityTicks),
            DisplaySliceSize: p.DisplaySliceTicks == 0 ? null : QuantityScale.FromTicks(p.DisplaySliceTicks));
    }

    // ========================================================================
    // Row 2: OrderCancel ↔ CancelOrderCommand
    //   Mirrors TranslationFixtures.ToInternal(OrderCancel) lines 125-128.
    // ========================================================================

    public static CancelOrderCommand ToInternalCancel(StrategyProto.OrderCancel p, string resolvedClientId)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedClientId);
        if (IsReservedClientId(resolvedClientId))
        {
            throw new InvalidOperationException(
                $"Reserved ClientId '{resolvedClientId}' — caller must reject with REJECT_REASON_STRUCTURAL before invoking translator.");
        }
        return new CancelOrderCommand(
            ClientId: resolvedClientId,
            OrderId: p.OrderId,
            InstrumentId: ToInternalInstrument(p.Instrument));
    }

    // ========================================================================
    // Row 3: OrderReplace ↔ ReplaceOrderCommand
    //   Mirrors TranslationFixtures.ToInternal(OrderReplace) lines 144-149.
    // ========================================================================

    public static ReplaceOrderCommand ToInternalReplace(StrategyProto.OrderReplace p, string resolvedClientId)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedClientId);
        if (IsReservedClientId(resolvedClientId))
        {
            throw new InvalidOperationException(
                $"Reserved ClientId '{resolvedClientId}' — caller must reject with REJECT_REASON_STRUCTURAL before invoking translator.");
        }
        return new ReplaceOrderCommand(
            ClientId: resolvedClientId,
            OrderId: p.OrderId,
            NewPriceTicks: p.NewPriceTicks == 0 ? null : p.NewPriceTicks,
            NewQuantity: p.NewQuantityTicks == 0 ? null : QuantityScale.FromTicks(p.NewQuantityTicks),
            InstrumentId: ToInternalInstrument(p.Instrument));
    }

    // ========================================================================
    // Inbound (HTTP-bound): BidMatrixSubmit → BidMatrixDto JSON for HTTP POST to DAH.
    //
    // Gateway DOES NOT publish bid matrices on RabbitMQ — they go directly to
    // dah-auction:8080/auction/bid (ARCHITECTURE.md §4 + Phase 05 D-09 handoff).
    // The body shape mirrors AuctionProto.BidMatrix → BidMatrixDto (proto BidStep
    // ↔ BidStepDto is 1:1 by field name: PriceTicks, QuantityTicks).
    // ========================================================================

    public static string ToBidMatrixJson(StrategyProto.BidMatrixSubmit p, string resolvedTeamName)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(p.Matrix);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedTeamName);

        var matrix = p.Matrix;
        var buy = new BidStepDto[matrix.BuySteps.Count];
        for (var i = 0; i < matrix.BuySteps.Count; i++)
        {
            buy[i] = new BidStepDto(matrix.BuySteps[i].PriceTicks, matrix.BuySteps[i].QuantityTicks);
        }
        var sell = new BidStepDto[matrix.SellSteps.Count];
        for (var i = 0; i < matrix.SellSteps.Count; i++)
        {
            sell[i] = new BidStepDto(matrix.SellSteps[i].PriceTicks, matrix.SellSteps[i].QuantityTicks);
        }
        // Gateway resolves team_name from the registered ClientId; the proto field
        // (matrix.team_name) is ignored on ingress — it is whatever the team typed
        // in their client and is not authoritative.
        var dto = new BidMatrixDto(
            TeamName: resolvedTeamName,
            QuarterId: matrix.QuarterId,
            BuySteps: buy,
            SellSteps: sell);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ========================================================================
    // Internal enum helpers — shared with OutboundTranslator. Mirrors
    // TranslationFixtures.cs lines 357-387 (Side / OrderType helpers).
    // ========================================================================

    internal static string SideEnumToString(MarketProto.Side s) => s switch
    {
        MarketProto.Side.Buy => "Buy",
        MarketProto.Side.Sell => "Sell",
        _ => throw new ArgumentException($"Unknown side: {s}", nameof(s)),
    };

    internal static MarketProto.Side SideStringToEnum(string s) => s switch
    {
        "Buy" => MarketProto.Side.Buy,
        "Sell" => MarketProto.Side.Sell,
        _ => throw new ArgumentException($"Unknown side: {s}", nameof(s)),
    };

    internal static string OrderTypeEnumToString(MarketProto.OrderType t) => t switch
    {
        MarketProto.OrderType.Limit => "Limit",
        MarketProto.OrderType.Market => "Market",
        MarketProto.OrderType.Iceberg => "Iceberg",
        MarketProto.OrderType.Fok => "FillOrKill",
        _ => throw new ArgumentException($"Unknown order type: {t}", nameof(t)),
    };

    internal static MarketProto.OrderType OrderTypeStringToEnum(string s) => s switch
    {
        "Limit" => MarketProto.OrderType.Limit,
        "Market" => MarketProto.OrderType.Market,
        "Iceberg" => MarketProto.OrderType.Iceberg,
        "FillOrKill" => MarketProto.OrderType.Fok,
        _ => throw new ArgumentException($"Unknown order type: {s}", nameof(s)),
    };

    // ========================================================================
    // Internal Instrument helpers — mirrors TranslationFixtures.cs lines 57-72.
    // ========================================================================

    internal static InstrumentIdDto ToInternalInstrument(MarketProto.Instrument p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new InstrumentIdDto(
            DeliveryArea: p.DeliveryArea,
            DeliveryPeriodStart: DateTimeOffset.FromUnixTimeMilliseconds(p.DeliveryPeriodStartNs / 1_000_000L),
            DeliveryPeriodEnd: DateTimeOffset.FromUnixTimeMilliseconds(p.DeliveryPeriodEndNs / 1_000_000L));
    }

    internal static MarketProto.Instrument ToProtoInstrument(
        InstrumentIdDto d,
        string instrumentId,
        MarketProto.ProductType productType)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new MarketProto.Instrument
        {
            InstrumentId = instrumentId ?? string.Empty,
            DeliveryArea = d.DeliveryArea,
            DeliveryPeriodStartNs = d.DeliveryPeriodStart.ToUnixTimeMilliseconds() * 1_000_000L,
            DeliveryPeriodEndNs = d.DeliveryPeriodEnd.ToUnixTimeMilliseconds() * 1_000_000L,
            ProductType = productType,
        };
    }

    // ========================================================================
    // Shared enum helpers (Phase 04 / Phase 03) — mirrors TranslationFixtures.cs
    // lines 539-569 (ShockPersistence + Regime).
    // ========================================================================

    internal static string ShockPersistenceEnumToString(Bifrost.Contracts.Events.ShockPersistence p) => p switch
    {
        Bifrost.Contracts.Events.ShockPersistence.Round => "Round",
        Bifrost.Contracts.Events.ShockPersistence.Transient => "Transient",
        _ => throw new ArgumentException($"Unknown shock persistence: {p}", nameof(p)),
    };

    internal static Bifrost.Contracts.Events.ShockPersistence ShockPersistenceStringToEnum(string s) => s switch
    {
        "Round" => Bifrost.Contracts.Events.ShockPersistence.Round,
        "Transient" => Bifrost.Contracts.Events.ShockPersistence.Transient,
        _ => throw new ArgumentException($"Unknown shock persistence: {s}", nameof(s)),
    };

    internal static string RegimeEnumToString(Bifrost.Contracts.Events.Regime r) => r switch
    {
        Bifrost.Contracts.Events.Regime.Calm => "Calm",
        Bifrost.Contracts.Events.Regime.Trending => "Trending",
        Bifrost.Contracts.Events.Regime.Volatile => "Volatile",
        Bifrost.Contracts.Events.Regime.Shock => "Shock",
        _ => throw new ArgumentException($"Unknown regime: {r}", nameof(r)),
    };

    internal static Bifrost.Contracts.Events.Regime RegimeStringToEnum(string s) => s switch
    {
        "Calm" => Bifrost.Contracts.Events.Regime.Calm,
        "Trending" => Bifrost.Contracts.Events.Regime.Trending,
        "Volatile" => Bifrost.Contracts.Events.Regime.Volatile,
        "Shock" => Bifrost.Contracts.Events.Regime.Shock,
        _ => throw new ArgumentException($"Unknown regime: {s}", nameof(s)),
    };

    // RoundState enum mapping — mirrors round.proto. The orchestrator publishes
    // the wire-side string form ("IterationOpen", "RoundOpen", "Gate", etc.); the
    // Phase 06 RoundStateChangedPayload carries it as `string State`.
    internal static string RoundStateEnumToString(Bifrost.Contracts.Round.State s) => s switch
    {
        Bifrost.Contracts.Round.State.Unspecified => "Unspecified",
        Bifrost.Contracts.Round.State.IterationOpen => "IterationOpen",
        Bifrost.Contracts.Round.State.AuctionOpen => "AuctionOpen",
        Bifrost.Contracts.Round.State.AuctionClosed => "AuctionClosed",
        Bifrost.Contracts.Round.State.RoundOpen => "RoundOpen",
        Bifrost.Contracts.Round.State.Gate => "Gate",
        Bifrost.Contracts.Round.State.Settled => "Settled",
        Bifrost.Contracts.Round.State.Aborted => "Aborted",
        _ => throw new ArgumentException($"Unknown round state: {s}", nameof(s)),
    };

    internal static Bifrost.Contracts.Round.State RoundStateStringToEnum(string s) => s switch
    {
        "Unspecified" => Bifrost.Contracts.Round.State.Unspecified,
        "IterationOpen" => Bifrost.Contracts.Round.State.IterationOpen,
        "AuctionOpen" => Bifrost.Contracts.Round.State.AuctionOpen,
        "AuctionClosed" => Bifrost.Contracts.Round.State.AuctionClosed,
        "RoundOpen" => Bifrost.Contracts.Round.State.RoundOpen,
        "Gate" => Bifrost.Contracts.Round.State.Gate,
        "Settled" => Bifrost.Contracts.Round.State.Settled,
        "Aborted" => Bifrost.Contracts.Round.State.Aborted,
        _ => throw new ArgumentException($"Unknown round state: {s}", nameof(s)),
    };
}
