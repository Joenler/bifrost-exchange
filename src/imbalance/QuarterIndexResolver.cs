using Bifrost.Contracts.Internal;

namespace Bifrost.Imbalance;

/// <summary>
/// Resolves a Phase-02-shaped instrument to a per-quarter imbalance-settlement index.
/// Returns 0..3 for the four 15-minute quarter instruments (Q1..Q4) and null for the
/// hour instrument (which carries no imbalance price). The resolver is purely structural:
/// it infers the quarter from the delivery-period start offset within its hour plus a
/// duration check, so it works against any TradingCalendar hour without hard-coded
/// delivery-date literals.
/// </summary>
public sealed class QuarterIndexResolver
{
    private const int QuarterDurationMinutes = 15;
    private const int HourDurationMinutes = 60;

    /// <summary>
    /// Resolve by <see cref="InstrumentIdDto"/>. Returns 0..3 for quarter instruments,
    /// null for hour or unrecognized instruments.
    /// </summary>
    public int? Resolve(InstrumentIdDto instrument)
    {
        if (instrument is null)
        {
            return null;
        }

        var duration = instrument.DeliveryPeriodEnd - instrument.DeliveryPeriodStart;
        if (duration.TotalMinutes != QuarterDurationMinutes)
        {
            // Hour (60 min) or anything else → no imbalance contribution.
            return null;
        }

        // Quarter-start minute within its hour: 0 → Q1 (0), 15 → Q2 (1), 30 → Q3 (2), 45 → Q4 (3).
        var minute = instrument.DeliveryPeriodStart.Minute;
        return minute switch
        {
            0 => 0,
            15 => 1,
            30 => 2,
            45 => 3,
            _ => null,
        };
    }

    /// <summary>
    /// Convenience overload keyed by the canonical routing-key form
    /// "&lt;area&gt;.&lt;startMinute&gt;-&lt;endMinute&gt;" (see
    /// <c>InstrumentId.ToRoutingKey()</c>). The fill-consumer uses this shape when it
    /// cannot attach the DTO directly.
    /// </summary>
    public int? Resolve(string instrumentRoutingKey)
    {
        if (string.IsNullOrEmpty(instrumentRoutingKey))
        {
            return null;
        }

        // Format: "<area>.<yyyyMMddHHmm>-<yyyyMMddHHmm>"
        var dot = instrumentRoutingKey.IndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var window = instrumentRoutingKey.AsSpan(dot + 1);
        var dash = window.IndexOf('-');
        if (dash < 0 || window.Length != 2 * 12 + 1)
        {
            return null;
        }

        var startSpan = window[..dash];
        var endSpan = window[(dash + 1)..];

        if (!TryParseMinutes(startSpan, out var startMinuteOfHour, out var startYyyymmddHh) ||
            !TryParseMinutes(endSpan, out var endMinuteOfHour, out var endYyyymmddHh))
        {
            return null;
        }

        var sameHour = startYyyymmddHh == endYyyymmddHh;
        var quarterDelta = endMinuteOfHour - startMinuteOfHour;
        if (sameHour && quarterDelta == QuarterDurationMinutes)
        {
            return startMinuteOfHour switch
            {
                0 => 0,
                15 => 1,
                30 => 2,
                45 => 3,
                _ => null,
            };
        }

        if (!sameHour && startMinuteOfHour == 45 && endMinuteOfHour == 0)
        {
            // Q4 boundary crosses into the next hour.
            return 3;
        }

        // Hour instrument: start minute 0, end minute 0, hour rollover.
        if (!sameHour && startMinuteOfHour == 0 && endMinuteOfHour == 0 && quarterDelta != 0)
        {
            return null;
        }

        return null;
    }

    private static bool TryParseMinutes(ReadOnlySpan<char> yyyymmddhhmm, out int minuteOfHour, out string yyyymmddhh)
    {
        minuteOfHour = -1;
        yyyymmddhh = string.Empty;
        if (yyyymmddhhmm.Length != 12)
        {
            return false;
        }

        if (!int.TryParse(yyyymmddhhmm[^2..], out var mm))
        {
            return false;
        }

        minuteOfHour = mm;
        yyyymmddhh = yyyymmddhhmm[..10].ToString();
        return true;
    }
}
