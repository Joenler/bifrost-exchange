using System.Collections.Concurrent;
using Bifrost.Exchange.Domain;

namespace Bifrost.Exchange.Application;

public sealed class InstrumentRegistry
{
    private readonly ConcurrentDictionary<InstrumentId, MatchingEngine> _engines;

    public InstrumentRegistry(IReadOnlyList<MatchingEngine> engines)
    {
        _engines = new ConcurrentDictionary<InstrumentId, MatchingEngine>(
            engines.Select(e => new KeyValuePair<InstrumentId, MatchingEngine>(e.Book.InstrumentId, e)));
    }

    public MatchingEngine? TryGet(InstrumentId id) =>
        _engines.GetValueOrDefault(id);

    public bool TryAdd(MatchingEngine engine) =>
        _engines.TryAdd(engine.Book.InstrumentId, engine);

    public IReadOnlyList<MatchingEngine> GetAllEngines() =>
        _engines.Values.ToList();

    public IReadOnlyCollection<InstrumentId> Instruments => _engines.Keys.ToList();

    /// <summary>
    /// Returns the quarter-hour instruments (15-minute duration) in deterministic
    /// order: DeliveryPeriod.Start ascending, then canonical routing-key ascending
    /// for tie-break. The one-hour instrument is intentionally excluded — the DAH
    /// auction clears only quarters, consistent with the project-wide imbalance
    /// settlement invariant that settlement is QH-only.
    /// </summary>
    /// <remarks>
    /// Filter uses duration arithmetic (End - Start == 15 minutes) rather than
    /// string parsing of the InstrumentId — this keeps the helper robust against
    /// future instrument-set rotation (real round timelines replacing the current
    /// 9999-01-01 synthetic dates).
    /// </remarks>
    public IReadOnlyList<InstrumentId> GetQuarterInstruments() =>
        _engines.Keys
            .Where(id => (id.DeliveryPeriod.End - id.DeliveryPeriod.Start) == TimeSpan.FromMinutes(15))
            .OrderBy(id => id.DeliveryPeriod.Start.UtcTicks)
            .ThenBy(id => id.ToRoutingKey(), StringComparer.Ordinal)
            .ToList();
}
