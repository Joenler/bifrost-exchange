using Bifrost.Gateway.Translation;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// Tier 1 of the ADR-0004 chain. Validates the gRPC oneof shape itself —
/// empty oneof, missing instrument, non-positive ticks, iceberg-slice violations,
/// and reserved client_id literals. Defence-in-depth for SPEC req 9 on top of the
/// InboundTranslator boundary check.
/// </summary>
public static class StructuralGuard
{
    public static GuardResult Check(StrategyProto.StrategyCommand cmd)
    {
        if (cmd is null || cmd.CommandCase == StrategyProto.StrategyCommand.CommandOneofCase.None)
            return GuardResult.Reject(StrategyProto.RejectReason.Structural, "empty command oneof");

        // Defence-in-depth: also check embedded client_id against reserved literals
        // (case-insensitive). The InboundTranslator already enforces this at the
        // boundary; this is a belt-and-braces guard against future translator paths
        // that might bypass it.
        var embeddedClientId = ExtractEmbeddedClientId(cmd);
        if (!string.IsNullOrEmpty(embeddedClientId) && InboundTranslator.IsReservedClientId(embeddedClientId))
            return GuardResult.Reject(StrategyProto.RejectReason.Structural, $"reserved client_id '{embeddedClientId}'");

        switch (cmd.CommandCase)
        {
            case StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit:
                return CheckSubmit(cmd.OrderSubmit);
            case StrategyProto.StrategyCommand.CommandOneofCase.OrderCancel:
                return CheckCancel(cmd.OrderCancel);
            case StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace:
                return CheckReplace(cmd.OrderReplace);

            // BidMatrixSubmit handled out-of-band (HTTP forward to dah-auction); the gateway
            // does not forward it on RabbitMQ — Phase 05 D-09 + ARCHITECTURE.md §4.
            case StrategyProto.StrategyCommand.CommandOneofCase.BidMatrixSubmit:
                return GuardResult.Reject(StrategyProto.RejectReason.Structural,
                    "BidMatrixSubmit must be POSTed directly to dah-auction:8080/auction/bid (gateway does not forward — Phase 05 D-09)");

            // Register is handled before the chain; reject if it appears mid-stream.
            case StrategyProto.StrategyCommand.CommandOneofCase.Register:
                return GuardResult.Reject(StrategyProto.RejectReason.Structural, "Register must be the first frame only");

            default:
                return GuardResult.Reject(StrategyProto.RejectReason.Structural, $"unknown command case {cmd.CommandCase}");
        }
    }

    private static GuardResult CheckSubmit(StrategyProto.OrderSubmit p)
    {
        if (p.Instrument is null)
            return GuardResult.Reject(StrategyProto.RejectReason.Structural, "missing instrument");
        if (p.QuantityTicks <= 0)
            return GuardResult.Reject(StrategyProto.RejectReason.Structural, "quantity_ticks must be > 0");
        if (p.OrderType == MarketProto.OrderType.Limit && p.PriceTicks <= 0)
            return GuardResult.Reject(StrategyProto.RejectReason.Structural, "limit order requires price_ticks > 0");
        if (p.DisplaySliceTicks > 0 && p.DisplaySliceTicks > p.QuantityTicks)
            return GuardResult.Reject(StrategyProto.RejectReason.Structural, "display_slice_ticks > quantity_ticks");
        return GuardResult.Ok;
    }

    private static GuardResult CheckCancel(StrategyProto.OrderCancel p)
    {
        if (p.OrderId <= 0)
            return GuardResult.Reject(StrategyProto.RejectReason.Structural, "missing order_id");
        return GuardResult.Ok;
    }

    private static GuardResult CheckReplace(StrategyProto.OrderReplace p)
    {
        if (p.OrderId <= 0)
            return GuardResult.Reject(StrategyProto.RejectReason.Structural, "missing order_id");
        return GuardResult.Ok;
    }

    private static string? ExtractEmbeddedClientId(StrategyProto.StrategyCommand cmd) => cmd.CommandCase switch
    {
        StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit => cmd.OrderSubmit?.ClientId,
        StrategyProto.StrategyCommand.CommandOneofCase.OrderCancel => cmd.OrderCancel?.ClientId,
        StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace => cmd.OrderReplace?.ClientId,
        _ => null,
    };
}
