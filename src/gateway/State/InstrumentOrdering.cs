namespace Bifrost.Gateway.State;

/// <summary>
/// Deterministic ordering of the 5 BIFROST instruments for the PositionSnapshot
/// burst on RegisterAck (D-06a). Matches Phase 02's TradingCalendar single-area
/// 1-hour + 4-quarter shape.
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
}
