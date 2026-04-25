namespace Bifrost.Gateway.Dispatch;

/// <summary>
/// Stable cohort assignment for ForecastDispatcher (D-15). Maps a teamName to a
/// cohort index in [0, cohortCount). Stable across reconnects (no runtime state):
/// the same teamName + same cohortCount always returns the same cohort.
///
/// Uses FNV-1a 32-bit hashing instead of <see cref="string.GetHashCode()"/>:
/// the BCL's String.GetHashCode is randomized per process by default since
/// .NET Core 2.1, which would break "stable across reconnects" if a team ever
/// reconnected to a freshly-restarted gateway. FNV-1a has no per-process salt,
/// no allocation, and is deterministic across .NET runtime versions.
/// </summary>
public static class CohortAssignment
{
    private const uint FnvPrime = 16777619u;
    private const uint FnvOffset = 2166136261u;

    public static int CohortFor(string teamName, int cohortCount)
    {
        if (cohortCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(cohortCount), "cohortCount must be > 0");
        if (string.IsNullOrEmpty(teamName))
            throw new ArgumentException("teamName empty", nameof(teamName));

        var hash = FnvOffset;
        for (var i = 0; i < teamName.Length; i++)
        {
            hash ^= teamName[i];
            hash *= FnvPrime;
        }
        return (int)(hash % (uint)cohortCount);
    }
}
