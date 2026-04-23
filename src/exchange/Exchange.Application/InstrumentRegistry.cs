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
}
