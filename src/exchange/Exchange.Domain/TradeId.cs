namespace Bifrost.Exchange.Domain;

public readonly record struct TradeId(long Value)
{
    public override string ToString() => Value.ToString();
}
