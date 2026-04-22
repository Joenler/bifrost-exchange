// tests/LintFenceFixtures/UsesConcurrentDictionaryCompound.cs
// EXPECTED: lint-concurrent-dictionary.sh emits 3 ::error:: lines (one per shape)
//           AND 1 ::notice:: line (for the escape-valve FormatMessage case).
//           BannedApiAnalyzers does NOT fire on this file — ripgrep is the authority.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Bifrost.LintFenceFixtures;

public sealed class Violation_ConcurrentDictionaryCompound
{
    private readonly ConcurrentDictionary<string, int> _positions = new();
    private readonly ConcurrentDictionary<string, List<int>> _orders = new();
    private readonly ConcurrentDictionary<string, string> _cachedFormatters = new();

    // Shape (a): GetOrAdd with side-effecting (statement-bodied) factory.
    // Two concurrent calls with the same key can both execute the factory → double side effects.
    public void AddOrder_ShapeA(string clientId, int orderId)
    {
        _orders.GetOrAdd(clientId, cid => {
            Console.WriteLine($"New client: {cid}");   // side effect
            return new List<int> { orderId };
        });
    }

    // Shape (b): TryGetValue followed by mutation on the same key.
    // Not atomic — between TryGetValue and the indexer assignment, another thread races.
    public void IncrementPosition_ShapeB(string clientId, int delta)
    {
        if (_positions.TryGetValue(clientId, out var current))
        {
            _positions[clientId] = current + delta;
        }
    }

    // Shape (c): AddOrUpdate where updateValueFactory has a mutating body.
    // The updater itself is atomic, but mutating external state via `existing.Add` can happen
    // twice under contention (AddOrUpdate retries its updater on CAS miss).
    public void AppendOrder_ShapeC(string clientId, int orderId)
    {
        _orders.AddOrUpdate(
            clientId,
            _ => new List<int> { orderId },
            (_, existing) => {
                existing.Add(orderId);   // mutates shared List
                return existing;
            });
    }

    // ALLOWED with escape valve: if the author can prove this ConcurrentDictionary is NOT
    // on scoring-relevant state (e.g., it's a cache of log-format strings), they opt out:
    // bifrost-lint: compound-ok — log formatter cache, not scoring-relevant
    public string FormatMessage(string key)
    {
        return _cachedFormatters.GetOrAdd(key, k => { return $"[{k}]"; });
    }
}
