using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Translation;

/// <summary>
/// Bidirectional <see cref="StrategyProto.RejectReason"/> ↔ string DTO map.
///
/// The string form is the on-the-wire DTO value (Bifrost.Contracts.Internal.Events
/// .OrderRejectedEvent serializes <c>Reason</c> as a JSON string). The mapping is
/// carried verbatim from <c>tests/Bifrost.Contracts.Translation.Tests/TranslationFixtures.cs</c>
/// (RejectReasonEnumToString / RejectReasonStringToEnum, lines 389-421) so the
/// existing CONT-07 byte-equivalence suite continues to pass against this
/// production code.
///
/// Any new <see cref="StrategyProto.RejectReason"/> enum value introduced in a
/// future strategy.proto bump fails the build via the default arm — this is the
/// forcing function that requires the translator to be updated alongside the
/// proto.
/// </summary>
public static class RejectReasonMap
{
    public static string EnumToString(StrategyProto.RejectReason reason) => reason switch
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
        _ => throw new ArgumentException($"Unknown reject reason: {reason}", nameof(reason)),
    };

    public static StrategyProto.RejectReason StringToEnum(string s) => s switch
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
        _ => throw new ArgumentException($"Unknown reject reason: {s}", nameof(s)),
    };
}
