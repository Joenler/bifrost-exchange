namespace Bifrost.Exchange.Domain;

public readonly record struct InstrumentId(DeliveryArea DeliveryArea, DeliveryPeriod DeliveryPeriod)
{
    public string ToRoutingKey() =>
        $"{DeliveryArea.Value}.{DeliveryPeriod.Start:yyyyMMddHHmm}-{DeliveryPeriod.End:yyyyMMddHHmm}";

    public override string ToString() => $"{DeliveryArea}.{DeliveryPeriod}";
}
