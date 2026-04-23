namespace Bifrost.Exchange.Domain;

public readonly record struct DeliveryArea(string Value)
{
    public override string ToString() => Value;
}
