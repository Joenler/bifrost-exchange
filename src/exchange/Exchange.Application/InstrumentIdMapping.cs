using Bifrost.Contracts.Internal;
using Bifrost.Exchange.Domain;

namespace Bifrost.Exchange.Application;

internal static class InstrumentIdMapping
{
    public static InstrumentIdDto ToDto(InstrumentId id) =>
        new(id.DeliveryArea.Value, id.DeliveryPeriod.Start, id.DeliveryPeriod.End);

    public static InstrumentId ToDomain(InstrumentIdDto dto) =>
        new(new DeliveryArea(dto.DeliveryArea),
            new DeliveryPeriod(dto.DeliveryPeriodStart, dto.DeliveryPeriodEnd));
}
