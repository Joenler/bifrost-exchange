using Bifrost.Contracts.Internal;

namespace Bifrost.Gateway.State;

/// <summary>
/// Deterministic ordering of the 5 BIFROST instruments for the PositionSnapshot
/// burst on RegisterAck (D-06a). Matches Phase 02's TradingCalendar single-area
/// 1-hour + 4-quarter shape (synthetic 9999-01-01 delivery date).
/// </summary>
public static class InstrumentOrdering
{
    /// <summary>5 instrument IDs in canonical order.</summary>
    public static readonly IReadOnlyList<string> CanonicalIds = new[]
    {
        "H1", "Q1", "Q2", "Q3", "Q4",
    };

    public static int IndexOf(string instrumentId)
    {
        for (var i = 0; i < CanonicalIds.Count; i++)
            if (CanonicalIds[i] == instrumentId) return i;
        return -1;
    }

    public static int Slots => CanonicalIds.Count;

    /// <summary>
    /// Map a wire-level <see cref="InstrumentIdDto"/> to its canonical slot
    /// index (0..4). Compares the delivery period against the Phase 02
    /// TradingCalendar synthetic 9999-01-01 shape: H1 = full hour, Q1..Q4 =
    /// 15-minute slices. Returns -1 if no match (Plan 06 will swap in a real
    /// instrument-catalog lookup once <c>IRoundStateSource</c> carries it).
    /// </summary>
    public static int IndexOfDto(InstrumentIdDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var startOffset = dto.DeliveryPeriodStart - hourStart;
        var span = dto.DeliveryPeriodEnd - dto.DeliveryPeriodStart;
        // Hour: starts at hourStart, lasts 60 minutes.
        if (startOffset == TimeSpan.Zero && span == TimeSpan.FromHours(1)) return 0;
        if (span != TimeSpan.FromMinutes(15)) return -1;
        // Quarter buckets: Q1=0..15, Q2=15..30, Q3=30..45, Q4=45..60.
        if (startOffset == TimeSpan.Zero) return 1;
        if (startOffset == TimeSpan.FromMinutes(15)) return 2;
        if (startOffset == TimeSpan.FromMinutes(30)) return 3;
        if (startOffset == TimeSpan.FromMinutes(45)) return 4;
        return -1;
    }

    /// <summary>Inverse of <see cref="IndexOfDto"/> — slot index → wire DTO.</summary>
    public static InstrumentIdDto DtoFor(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= Slots)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return slotIndex switch
        {
            0 => new InstrumentIdDto("DE", hourStart, hourStart.AddHours(1)),
            1 => new InstrumentIdDto("DE", hourStart, hourStart.AddMinutes(15)),
            2 => new InstrumentIdDto("DE", hourStart.AddMinutes(15), hourStart.AddMinutes(30)),
            3 => new InstrumentIdDto("DE", hourStart.AddMinutes(30), hourStart.AddMinutes(45)),
            4 => new InstrumentIdDto("DE", hourStart.AddMinutes(45), hourStart.AddHours(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(slotIndex)),
        };
    }
}
