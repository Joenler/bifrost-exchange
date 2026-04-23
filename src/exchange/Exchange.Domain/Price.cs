namespace Bifrost.Exchange.Domain;

public readonly record struct Price(long Ticks) : IComparable<Price>
{
    public int CompareTo(Price other) => Ticks.CompareTo(other.Ticks);

    public static bool operator >(Price left, Price right) => left.Ticks > right.Ticks;
    public static bool operator <(Price left, Price right) => left.Ticks < right.Ticks;
    public static bool operator >=(Price left, Price right) => left.Ticks >= right.Ticks;
    public static bool operator <=(Price left, Price right) => left.Ticks <= right.Ticks;

    public override string ToString() => Ticks.ToString();
}