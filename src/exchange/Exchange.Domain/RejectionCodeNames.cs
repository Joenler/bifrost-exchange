namespace Bifrost.Exchange.Domain;

/// <summary>
/// Cached static <see cref="string"/> representations of <see cref="RejectionCode"/> values
/// for use as the <c>rejection_code</c> Prometheus label value on
/// <c>ExchangeMetrics.Rejections.Add</c> emissions.
/// </summary>
/// <remarks>
/// Distinct from <see cref="RejectionCodeExtensions.ToDisplayString"/>:
/// <list type="bullet">
///   <item><c>ToDisplayString()</c> returns human-readable strings ("Order not found") for log
///     and <c>OrderRejectedEvent.Reason</c> use.</item>
///   <item><c>RejectionCodeNames</c> returns enum identifier strings ("OrderNotFound") for
///     metric tag use. Suitable as a Prometheus label value: low cardinality,
///     stable, grep-friendly.</item>
/// </list>
/// Both families are zero-allocation (compile-time string constants); <see cref="Get"/>
/// returns reference equality with the corresponding static field.
/// </remarks>
public static class RejectionCodeNames
{
    public static readonly string InsufficientLiquidityForFok = "InsufficientLiquidityForFok";
    public static readonly string OrderNotFound = "OrderNotFound";
    public static readonly string NotAuthorizedToCancel = "NotAuthorizedToCancel";
    public static readonly string NotAuthorizedToReplace = "NotAuthorizedToReplace";
    public static readonly string QuantityIncreaseNotSupported = "QuantityIncreaseNotSupported";
    public static readonly string NewQuantityBelowFilledAmount = "NewQuantityBelowFilledAmount";
    public static readonly string InvalidSide = "InvalidSide";
    public static readonly string InvalidOrderType = "InvalidOrderType";
    public static readonly string UnknownInstrument = "UnknownInstrument";
    public static readonly string DeliveryPeriodExpired = "DeliveryPeriodExpired";
    public static readonly string PriceNotAlignedToTickSize = "PriceNotAlignedToTickSize";
    public static readonly string QuantityBelowMinimum = "QuantityBelowMinimum";
    public static readonly string QuantityNotAlignedToStep = "QuantityNotAlignedToStep";
    public static readonly string DisplaySliceSizeBelowMinimum = "DisplaySliceSizeBelowMinimum";
    public static readonly string DisplaySliceSizeNotAlignedToStep = "DisplaySliceSizeNotAlignedToStep";
    public static readonly string LimitOrderRequiresPrice = "LimitOrderRequiresPrice";
    public static readonly string IcebergOrderRequiresPriceAndSliceSize = "IcebergOrderRequiresPriceAndSliceSize";
    public static readonly string FillOrKillOrderRequiresPrice = "FillOrKillOrderRequiresPrice";
    public static readonly string ExchangeClosed = "ExchangeClosed";

    public static string Get(RejectionCode code) => code switch
    {
        RejectionCode.InsufficientLiquidityForFok => InsufficientLiquidityForFok,
        RejectionCode.OrderNotFound => OrderNotFound,
        RejectionCode.NotAuthorizedToCancel => NotAuthorizedToCancel,
        RejectionCode.NotAuthorizedToReplace => NotAuthorizedToReplace,
        RejectionCode.QuantityIncreaseNotSupported => QuantityIncreaseNotSupported,
        RejectionCode.NewQuantityBelowFilledAmount => NewQuantityBelowFilledAmount,
        RejectionCode.InvalidSide => InvalidSide,
        RejectionCode.InvalidOrderType => InvalidOrderType,
        RejectionCode.UnknownInstrument => UnknownInstrument,
        RejectionCode.DeliveryPeriodExpired => DeliveryPeriodExpired,
        RejectionCode.PriceNotAlignedToTickSize => PriceNotAlignedToTickSize,
        RejectionCode.QuantityBelowMinimum => QuantityBelowMinimum,
        RejectionCode.QuantityNotAlignedToStep => QuantityNotAlignedToStep,
        RejectionCode.DisplaySliceSizeBelowMinimum => DisplaySliceSizeBelowMinimum,
        RejectionCode.DisplaySliceSizeNotAlignedToStep => DisplaySliceSizeNotAlignedToStep,
        RejectionCode.LimitOrderRequiresPrice => LimitOrderRequiresPrice,
        RejectionCode.IcebergOrderRequiresPriceAndSliceSize => IcebergOrderRequiresPriceAndSliceSize,
        RejectionCode.FillOrKillOrderRequiresPrice => FillOrKillOrderRequiresPrice,
        RejectionCode.ExchangeClosed => ExchangeClosed,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
