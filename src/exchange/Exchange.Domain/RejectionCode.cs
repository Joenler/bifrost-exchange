namespace Bifrost.Exchange.Domain;

public enum RejectionCode
{
    InsufficientLiquidityForFok,
    OrderNotFound,
    NotAuthorizedToCancel,
    NotAuthorizedToReplace,
    QuantityIncreaseNotSupported,
    NewQuantityBelowFilledAmount,

    InvalidSide,
    InvalidOrderType,
    UnknownInstrument,
    DeliveryPeriodExpired,
    PriceNotAlignedToTickSize,
    QuantityBelowMinimum,
    QuantityNotAlignedToStep,
    DisplaySliceSizeBelowMinimum,
    DisplaySliceSizeNotAlignedToStep,
    LimitOrderRequiresPrice,
    IcebergOrderRequiresPriceAndSliceSize,
    FillOrKillOrderRequiresPrice,
    ExchangeClosed,
}
