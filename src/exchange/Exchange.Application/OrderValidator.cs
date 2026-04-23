using Bifrost.Contracts.Internal.Commands;
using Bifrost.Exchange.Domain;
using Bifrost.Time;

namespace Bifrost.Exchange.Application;

public sealed class OrderValidator(
    ExchangeRulesConfig rules,
    InstrumentRegistry registry,
    IClock clock)
{
    public OrderValidationResult ValidateSubmit(SubmitOrderCommand cmd)
    {
        if (!Enum.TryParse<Side>(cmd.Side, true, out var side))
            return OrderValidationResult.Rejected(RejectionCode.InvalidSide, $"Invalid side: {cmd.Side}");

        if (!Enum.TryParse<OrderType>(cmd.OrderType, true, out var orderType))
            return OrderValidationResult.Rejected(RejectionCode.InvalidOrderType, $"Invalid order type: {cmd.OrderType}");

        var instrumentId = InstrumentIdMapping.ToDomain(cmd.InstrumentId);

        var engine = registry.TryGet(instrumentId);
        if (engine is null)
            return OrderValidationResult.Rejected(RejectionCode.UnknownInstrument, "Unknown instrument");

        if (instrumentId.DeliveryPeriod.HasExpired(clock.GetUtcNow()))
            return OrderValidationResult.Rejected(RejectionCode.DeliveryPeriodExpired, "Delivery period has expired");

        if (cmd.PriceTicks.HasValue && cmd.PriceTicks.Value % rules.TickSize != 0)
            return OrderValidationResult.Rejected(
                RejectionCode.PriceNotAlignedToTickSize,
                $"Price {cmd.PriceTicks.Value} not aligned to tick size {rules.TickSize}");

        if (cmd.Quantity < rules.MinQuantity)
            return OrderValidationResult.Rejected(
                RejectionCode.QuantityBelowMinimum,
                FormattableString.Invariant($"Quantity {cmd.Quantity} below minimum {rules.MinQuantity}"));

        if (cmd.Quantity % rules.QuantityStep != 0m)
            return OrderValidationResult.Rejected(
                RejectionCode.QuantityNotAlignedToStep,
                FormattableString.Invariant($"Quantity {cmd.Quantity} not aligned to step {rules.QuantityStep}"));

        if (cmd.DisplaySliceSize.HasValue)
        {
            if (cmd.DisplaySliceSize.Value < rules.MinQuantity)
                return OrderValidationResult.Rejected(
                    RejectionCode.DisplaySliceSizeBelowMinimum,
                    FormattableString.Invariant($"Display slice size {cmd.DisplaySliceSize.Value} below minimum {rules.MinQuantity}"));

            if (cmd.DisplaySliceSize.Value % rules.QuantityStep != 0m)
                return OrderValidationResult.Rejected(
                    RejectionCode.DisplaySliceSizeNotAlignedToStep,
                    FormattableString.Invariant($"Display slice size {cmd.DisplaySliceSize.Value} not aligned to step {rules.QuantityStep}"));
        }

        return OrderValidationResult.Valid(side, orderType, instrumentId, engine);
    }

    public OrderValidationResult ValidateCancel(CancelOrderCommand cmd)
    {
        var instrumentId = InstrumentIdMapping.ToDomain(cmd.InstrumentId);

        var engine = registry.TryGet(instrumentId);
        if (engine is null)
            return OrderValidationResult.Rejected(RejectionCode.UnknownInstrument, "Unknown instrument");

        return OrderValidationResult.Valid(default, default, instrumentId, engine);
    }

    public OrderValidationResult ValidateReplace(ReplaceOrderCommand cmd)
    {
        var instrumentId = InstrumentIdMapping.ToDomain(cmd.InstrumentId);

        var engine = registry.TryGet(instrumentId);
        if (engine is null)
            return OrderValidationResult.Rejected(RejectionCode.UnknownInstrument, "Unknown instrument");

        if (cmd.NewPriceTicks.HasValue && cmd.NewPriceTicks.Value % rules.TickSize != 0)
            return OrderValidationResult.Rejected(
                RejectionCode.PriceNotAlignedToTickSize,
                $"Price {cmd.NewPriceTicks.Value} not aligned to tick size {rules.TickSize}");

        if (cmd.NewQuantity.HasValue)
        {
            if (cmd.NewQuantity.Value < rules.MinQuantity)
                return OrderValidationResult.Rejected(
                    RejectionCode.QuantityBelowMinimum,
                    FormattableString.Invariant($"Quantity {cmd.NewQuantity.Value} below minimum {rules.MinQuantity}"));

            if (cmd.NewQuantity.Value % rules.QuantityStep != 0m)
                return OrderValidationResult.Rejected(
                    RejectionCode.QuantityNotAlignedToStep,
                    FormattableString.Invariant($"Quantity {cmd.NewQuantity.Value} not aligned to step {rules.QuantityStep}"));
        }

        return OrderValidationResult.Valid(default, default, instrumentId, engine);
    }
}
