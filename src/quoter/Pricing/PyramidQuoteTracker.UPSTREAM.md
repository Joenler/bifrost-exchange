# Upstream — PyramidQuoteTracker.cs (and co-donated event records)

## Files

| Donated path (this folder) | Original path (Arena)                                                            | Arena commit SHA                           | Mutations applied |
|----------------------------|----------------------------------------------------------------------------------|--------------------------------------------|-------------------|
| `PyramidQuoteTracker.cs`         | `src/trader/ArenaTrader.Strategies.MarketMaker/PyramidQuoteTracker.cs`           | `3d94185edc8b1e12f119740c98f197a3b38f513e` | (1) Namespace `ArenaTrader.Strategies.MarketMaker` -> `Bifrost.Quoter.Pricing`. (2) `using ArenaTrader.Core;` -> `using Bifrost.Exchange.Domain;`. (3) `using ArenaTrader.Core.Events;` -> four `using <Name> = Bifrost.Quoter.Pricing.Events.<Name>;` aliases (one each for `OrderAccepted` / `OrderFill` / `OrderCancelled` / `OrderRejected`) — required to disambiguate against the same-named matching-engine-internal records that already live in `Bifrost.Exchange.Domain` (Phase 02 `MatchingEvents.cs`). (4) Added `using Bifrost.Quoter.Abstractions;` for `IOrderContext`. (5) Visibility widened: outer `internal sealed class PyramidQuoteTracker` -> `public sealed class`; inner `internal sealed class LevelOrder` and `internal sealed class LevelSet` widened to `public sealed class` (required so the public `GetOrCreate(InstrumentId)` return type is consistent — Rule 3 blocking fix CS0050). All other code byte-identical to Arena source. |
| `Events/OrderAccepted.cs`        | `src/trader/ArenaTrader.Core/Events/OrderAccepted.cs`                            | `42283d395e20f9ff5690e76b5948f80d5800db9a` | Namespace `ArenaTrader.Core.Events` -> `Bifrost.Quoter.Pricing.Events`. Added `using Bifrost.Exchange.Domain;` for `OrderId`/`InstrumentId`/`Side`/`OrderType`. Body verbatim. (See Divergence: hosted under `Pricing/Events/` sub-namespace to avoid clash with `Bifrost.Exchange.Domain.OrderAccepted` in the matching-engine donation.) |
| `Events/OrderFill.cs`            | `src/trader/ArenaTrader.Core/Events/OrderFill.cs`                                | `42283d395e20f9ff5690e76b5948f80d5800db9a` | Namespace `ArenaTrader.Core.Events` -> `Bifrost.Quoter.Pricing.Events`. Added `using Bifrost.Exchange.Domain;` for `OrderId`/`InstrumentId`/`Side`/`TradeId`. Body verbatim. (No clash with Domain — Domain has `TradeFilled`, not `OrderFill`.) |
| `Events/OrderCancelled.cs`       | `src/trader/ArenaTrader.Core/Events/OrderCancelled.cs`                           | `42283d395e20f9ff5690e76b5948f80d5800db9a` | Namespace `ArenaTrader.Core.Events` -> `Bifrost.Quoter.Pricing.Events`. Added `using Bifrost.Exchange.Domain;` for `OrderId`/`InstrumentId`. Body verbatim. (See Divergence: same name as `Bifrost.Exchange.Domain.OrderCancelled`; resolved via using-alias.) |
| `Events/OrderRejected.cs`        | `src/trader/ArenaTrader.Core/Events/OrderRejected.cs`                            | `42283d395e20f9ff5690e76b5948f80d5800db9a` | Namespace `ArenaTrader.Core.Events` -> `Bifrost.Quoter.Pricing.Events`. Added `using Bifrost.Exchange.Domain;` for `OrderId`. Body verbatim. (See Divergence: same name as `Bifrost.Exchange.Domain.OrderRejected`; resolved via using-alias.) |

## Divergence rationale

**Visibility widening (PyramidQuoteTracker).** Arena's `internal sealed class` was scoped to the
MarketMaker assembly + InternalsVisibleTo Tests. BIFROST instead widens to `public sealed class`
so the consumer (`Quoter.cs`, future plan) and the test assembly (`Bifrost.Quoter.Tests`) can use
the type without an `[InternalsVisibleTo]` attribute — chosen for simplicity per the donation plan.
The two inner classes (`LevelOrder`, `LevelSet`) had to be widened in lockstep because the public
`GetOrCreate(InstrumentId)` returns `LevelSet` — without the inner widening C# rejects the build
with CS0050 inconsistent-accessibility.

**IOrderContext shim.** Arena's `IOrderContext` (in `ArenaTrader.Core`) carries a wider surface
(market / iceberg / FOK submitters, OTR ratio query, working-orders enumeration). The BIFROST shim
in `Bifrost.Quoter.Abstractions.IOrderContext` is the narrow subset the tracker actually consumes
(`CancelOrder` only — the SubmitLimitOrder / ReplaceOrder / GetOrder / Logger members on the
shim are there for the future Quoter consumer, not for the tracker). The `using
Bifrost.Quoter.Abstractions;` directive is the only body-level addition required by the re-target.

**Co-donation of CorrelationId + four event records.** Arena's `CorrelationId` (in
`ArenaTrader.Core`) and the four `OrderAccepted` / `OrderFill` / `OrderCancelled` / `OrderRejected`
records (in `ArenaTrader.Core.Events`) are not present in `Bifrost.Exchange.Domain` (Phase 02
deliberately scoped to matching-engine primitives). They are co-donated under the quoter, matching
the precedent already established for `QuotableRange` / `HittableRange` / `SideBias` /
`RandomExtensions` in earlier plans. `CorrelationId` provenance lives in
`AvellanedaStoikov.UPSTREAM.md` (it was added during the sibling Abstractions shim authoring);
the four event records are documented in this file.

**Namespace clash with Phase 02 matching-engine records.** Phase 02 already shipped
`Bifrost.Exchange.Domain.OrderAccepted`, `Bifrost.Exchange.Domain.OrderRejected`, and
`Bifrost.Exchange.Domain.OrderCancelled` records (matching-engine-internal events with
`ClientId` / `Price` / `Quantity` typed fields and no `CorrelationId`) — different shape,
different purpose. The quoter-side reconciliation events absolutely need `CorrelationId`
(that is the entire reason `PyramidQuoteTracker._pending` keys on it), so re-using the Domain
records is not viable.

The four co-donated event records are therefore hosted under a new `Pricing/Events/` subfolder
with namespace `Bifrost.Quoter.Pricing.Events` (instead of the bare `Bifrost.Quoter.Pricing`).
Inside `PyramidQuoteTracker.cs`, four `using <Name> = Bifrost.Quoter.Pricing.Events.<Name>;`
aliases pin every method-signature reference (`OnOrderAccepted(OrderAccepted accepted)` etc.)
to the quoter-side record. Without the aliases (or with a wildcard
`using Bifrost.Quoter.Pricing.Events;` directive plus the `using Bifrost.Exchange.Domain;`
directive both in scope) C# rejects the build with CS0104 ambiguous-reference. The
disambiguation is documented inline at the using-alias declarations as well.

**Clock.** Arena's source already takes `TimeProvider` via the constructor and routes every clock
read through `_timeProvider.GetUtcNow()` — no clock rewire was required. The bare `DateTimeOffset`
type appears only as the value of an `ImmutableDictionary<CorrelationId, DateTimeOffset>` field
(no calls to `DateTime.UtcNow` exist in the source), so the BannedSymbols analyzer is satisfied.

**ConcurrentDictionary audit (single-writer / non-compound rule).** Two `ConcurrentDictionary<,>`
fields are present (`_pending`, `_accepted`); both are accessed exclusively via non-compound
operations: indexer set (`dict[key] = value`), `TryGetValue`, and `TryRemove`. No `GetOrAdd` or
`AddOrUpdate` call sites exist. The repository lint hook
`.github/scripts/lint-concurrent-dictionary.sh` reports `clean` against `src/quoter` after the
port.

## Commit SHA lookup

```bash
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Strategies.MarketMaker/PyramidQuoteTracker.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/Events/OrderAccepted.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/Events/OrderFill.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/Events/OrderCancelled.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/Events/OrderRejected.cs
```
