namespace Bifrost.Quoter.Schedule;

/// <summary>
/// Bounded array-backed set with linear-scan dedup. Used by
/// <see cref="RegimeSchedule"/> to dedup MC-force nonces across the operator
/// command bus: a single MC console action may publish more than once if
/// retries hit the broker, and the quoter must apply the force exactly once.
/// <para>
/// Capacity is intentionally small (16 by default) so the linear scan stays
/// cache-friendly at MC frequency (≤ 1 force per second). When capacity fills,
/// the oldest entry is overwritten — acceptable for event ops because old
/// nonces are extremely unlikely to replay during a 10-minute round.
/// </para>
/// </summary>
public sealed class LruSet<T> where T : struct, IEquatable<T>
{
    private readonly T[] _slots;
    private int _writeIndex;
    private readonly object _lock = new();

    public LruSet(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must be positive.");
        _slots = new T[capacity];
    }

    /// <summary>
    /// Adds <paramref name="value"/> to the set. Returns <c>false</c> if the
    /// value is already present (dedup hit) without modifying state, otherwise
    /// inserts it (potentially evicting the oldest slot) and returns <c>true</c>.
    /// </summary>
    public bool Add(T value)
    {
        lock (_lock)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Equals(value))
                    return false;
            }

            _slots[_writeIndex] = value;
            _writeIndex = (_writeIndex + 1) % _slots.Length;
            return true;
        }
    }
}
