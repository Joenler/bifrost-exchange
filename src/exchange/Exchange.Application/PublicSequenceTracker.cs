using Bifrost.Exchange.Domain;

namespace Bifrost.Exchange.Application;

public sealed class PublicSequenceTracker
{
    private readonly Dictionary<InstrumentId, long> _sequences = new();

    public long Next(InstrumentId instrumentId)
    {
        if (!_sequences.TryGetValue(instrumentId, out var current))
        {
            current = 0;
        }

        current++;
        _sequences[instrumentId] = current;
        return current;
    }

    public long Current(InstrumentId instrumentId)
    {
        return _sequences.GetValueOrDefault(instrumentId, 0);
    }
}
