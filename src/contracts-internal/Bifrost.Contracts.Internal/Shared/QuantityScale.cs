namespace Bifrost.Contracts.Internal.Shared;

// TODO: finalize tick scale alongside the exchange engine.
// Byte-equivalence of the translation round-trip is independent of the absolute scale.
public static class QuantityScale
{
    private const long TicksPerUnit = 10_000;

    public static long ToTicks(decimal quantity) => (long)(quantity * TicksPerUnit);
    public static decimal FromTicks(long ticks) => (decimal)ticks / TicksPerUnit;
}
