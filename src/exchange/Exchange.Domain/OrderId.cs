namespace Bifrost.Exchange.Domain;

public readonly record struct OrderId(long Value)
{
    public override string ToString() => Value.ToString();
}