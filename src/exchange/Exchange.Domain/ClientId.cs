namespace Bifrost.Exchange.Domain;

public readonly record struct ClientId(string Value)
{
    public override string ToString() => Value;
}