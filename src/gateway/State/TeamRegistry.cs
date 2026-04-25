using Bifrost.Time;

namespace Bifrost.Gateway.State;

/// <summary>
/// Plain Dictionary&lt;string,TeamState&gt; protected by a single Monitor lock.
/// Pitfall 3 + BOOT-04 CI fence: lock-free concurrent dictionaries are BANNED on this
/// state because register/reconnect is a compound get-or-create followed by mutation.
/// Lock order: this._lock → teamState.StateLock per TeamState.cs file header.
/// </summary>
public sealed class TeamRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TeamState> _byTeamName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeamState> _byClientId = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private long _nextClientIdSerial;

    private static readonly string[] ReservedClientIds = { "quoter", "dah-auction" };

    public TeamRegistry(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// SPEC req 1, 2, 9. teamName is the externally provided team_name from
    /// strategy.proto::Register. lastSeenSequence == 0 ⇒ fresh register; &gt; 0 ⇒ resume.
    /// Reserved names ("quoter", "dah-auction") rejected case-insensitive.
    /// </summary>
    public RegisterResult TryRegister(string teamName, long lastSeenSequence)
    {
        if (string.IsNullOrWhiteSpace(teamName))
            return new RegisterResult(false, null, 0, false, "team_name empty");
        foreach (var reserved in ReservedClientIds)
            if (string.Equals(teamName, reserved, StringComparison.OrdinalIgnoreCase))
                return new RegisterResult(false, null, 0, false, $"team_name '{teamName}' is reserved");

        lock (_lock)
        {
            if (!_byTeamName.TryGetValue(teamName, out var existing))
            {
                var clientId = AllocateClientId(teamName);
                var state = new TeamState(teamName, clientId, _clock.GetUtcNow());
                _byTeamName[teamName] = state;
                _byClientId[clientId] = state;
                return new RegisterResult(true, state, 0, false, null);
            }

            // Reconnect path: same teamName ⇒ original ClientId.
            // Determine resume-feasibility — ring-retention is whole-round (D-07a).
            long resumedFrom;
            bool reregisterRequired;
            lock (existing.StateLock)
            {
                if (lastSeenSequence == 0 || existing.Ring.IsRetained(lastSeenSequence))
                {
                    resumedFrom = lastSeenSequence;
                    reregisterRequired = false;
                }
                else
                {
                    resumedFrom = 0;
                    reregisterRequired = true;
                }
            }
            return new RegisterResult(true, existing, resumedFrom, reregisterRequired, null);
        }
    }

    public bool TryGetByClientId(string clientId, out TeamState? state)
    {
        lock (_lock)
        {
            return _byClientId.TryGetValue(clientId, out state);
        }
    }

    public void MarkDisconnected(TeamState state)
    {
        // Registry stays — reconnect-by-name is GW-02. State is preserved.
        // Plan 07 DisconnectHandler clears resting orders; here we only mark.
        ArgumentNullException.ThrowIfNull(state);
    }

    /// <summary>Snapshot of all teams, sorted by team_name (StringComparer.Ordinal). Lock-released before return.</summary>
    public TeamState[] SnapshotAll()
    {
        lock (_lock)
        {
            var copy = new TeamState[_byTeamName.Count];
            var i = 0;
            foreach (var kv in _byTeamName) copy[i++] = kv.Value;
            Array.Sort(copy, (a, b) => StringComparer.Ordinal.Compare(a.TeamName, b.TeamName));
            return copy;
        }
    }

    /// <summary>D-11: wipes every team's ring atomically. Two-phase to honor lock order.</summary>
    public void OnSettledToIterationOpen()
    {
        var teams = SnapshotAll();   // takes registry lock briefly, then releases
        foreach (var ts in teams)
        {
            lock (ts.StateLock) { ts.Ring.Wipe(); }
        }
    }

    private string AllocateClientId(string teamName)
    {
        // Opaque deterministic-but-unique ClientId. Phase 02 D-14: opaque string.
        // Use teamName + monotonic suffix so logs stay readable.
        var serial = ++_nextClientIdSerial;
        return $"team-{teamName}-{serial}";
    }
}
