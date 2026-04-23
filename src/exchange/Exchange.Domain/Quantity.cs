namespace Bifrost.Exchange.Domain;

public readonly record struct Quantity(decimal Value)
{
    public static Quantity Zero => new(0);

    public static Quantity operator +(Quantity left, Quantity right) => new(left.Value + right.Value);
    public static Quantity operator -(Quantity left, Quantity right) => new(left.Value - right.Value);
    public static Quantity Min(Quantity a, Quantity b) => new(Math.Min(a.Value, b.Value));

    public static bool operator >(Quantity left, Quantity right) => left.Value > right.Value;
    public static bool operator <(Quantity left, Quantity right) => left.Value < right.Value;
    public static bool operator >=(Quantity left, Quantity right) => left.Value >= right.Value;
    public static bool operator <=(Quantity left, Quantity right) => left.Value <= right.Value;

    public override string ToString() => Value.ToString();
}