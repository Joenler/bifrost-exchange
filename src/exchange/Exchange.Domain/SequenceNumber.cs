namespace Bifrost.Exchange.Domain;

public readonly record struct SequenceNumber(long Value) : IComparable<SequenceNumber>
{
    public int CompareTo(SequenceNumber other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}