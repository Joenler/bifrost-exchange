namespace Bifrost.Exchange.Domain;

public static class RejectionCodeExtensions
{
    public static string ToDisplayString(this RejectionCode code) => code switch
    {
        RejectionCode.InsufficientLiquidityForFok => "Insufficient liquidity for Fill-or-Kill order",
        RejectionCode.OrderNotFound => "Order not found",
        RejectionCode.NotAuthorizedToCancel => "Not authorized to cancel this order",
        RejectionCode.NotAuthorizedToReplace => "Not authorized to replace this order",
        RejectionCode.QuantityIncreaseNotSupported => "Quantity increase not supported",
        RejectionCode.NewQuantityBelowFilledAmount => "New quantity below already-filled amount",
        RejectionCode.InvalidSide => "Invalid side",
        RejectionCode.InvalidOrderType => "Invalid order type",
        RejectionCode.UnknownInstrument => "Unknown instrument",
        RejectionCode.DeliveryPeriodExpired => "Delivery period has expired",
        RejectionCode.PriceNotAlignedToTickSize => "Price not aligned to tick size",
        RejectionCode.QuantityBelowMinimum => "Quantity below minimum",
        RejectionCode.QuantityNotAlignedToStep => "Quantity not aligned to step",
        RejectionCode.DisplaySliceSizeBelowMinimum => "Display slice size below minimum",
        RejectionCode.DisplaySliceSizeNotAlignedToStep => "Display slice size not aligned to step",
        RejectionCode.LimitOrderRequiresPrice => "Limit order requires price",
        RejectionCode.IcebergOrderRequiresPriceAndSliceSize => "Iceberg order requires price and display slice size",
        RejectionCode.FillOrKillOrderRequiresPrice => "Fill-or-Kill order requires price",
        RejectionCode.ExchangeClosed => "Exchange closed at gate",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
