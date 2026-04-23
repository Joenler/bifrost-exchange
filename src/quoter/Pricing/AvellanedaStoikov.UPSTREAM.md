# Upstream — src/quoter/Pricing/

## Files

| Donated path (this folder) | Original path (Arena)                                            | Arena commit SHA                           | Mutations applied                                                                                                                                                                  |
|----------------------------|------------------------------------------------------------------|--------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `AvellanedaStoikov.cs`     | `src/trader/ArenaTrader.Pricing.MeritOrder/AvellanedaStoikov.cs` | `1f4dac579b7989b52508413e19a2d2b801b83a08` | Removed `using ArenaTrader.Core;` (co-donated value types live in `Bifrost.Quoter.Pricing` — see rows below). Namespace `ArenaTrader.Pricing.MeritOrder` → `Bifrost.Quoter.Pricing`. |
| `QuotableRange.cs`         | `src/trader/ArenaTrader.Core/QuotableRange.cs`                   | `cf72124776782aa67ad0a6e9b556a90804be7fcb` | Namespace `ArenaTrader.Core` → `Bifrost.Quoter.Pricing`.                                                                                                                           |
| `HittableRange.cs`         | `src/trader/ArenaTrader.Core/HittableRange.cs`                   | `cf72124776782aa67ad0a6e9b556a90804be7fcb` | Namespace `ArenaTrader.Core` → `Bifrost.Quoter.Pricing`.                                                                                                                           |
| `SideBias.cs`              | `src/trader/ArenaTrader.Core/SideBias.cs`                        | `cf72124776782aa67ad0a6e9b556a90804be7fcb` | Namespace `ArenaTrader.Core` → `Bifrost.Quoter.Pricing`.                                                                                                                           |
| `CorrelationId.cs`         | `src/trader/ArenaTrader.Core/CorrelationId.cs`                   | `655581c7045ca107aef9a92778bb8c4368919a4e` | Namespace `ArenaTrader.Core` → `Bifrost.Quoter.Pricing`. Body verbatim. Co-donated alongside the IOrderContext shim consumers; Phase 02 Exchange.Domain did not absorb it.        |

## Divergence rationale

No semantic divergence — pure-math port. Namespace rewrite reflects BIFROST monorepo
layout (ADR-0006).

`QuotableRange`, `HittableRange`, and `SideBias` are co-donated from
`Arena/src/trader/ArenaTrader.Core/` because the Phase 02 Exchange.Domain donation
deliberately scoped down to matching-engine primitives (`InstrumentId`, `Side`,
`Price`, `Order`, etc.) and did not absorb Arena's pricing-side value types.
Hosting them in `Bifrost.Quoter.Pricing` alongside `AvellanedaStoikov.cs` keeps the
quoter's pure-math surface self-contained and avoids forcing a back-port into
`Bifrost.Exchange.Domain`. Deviation noted: the planned `using
Bifrost.Exchange.Domain;` rewrite is replaced by deleting the `using ArenaTrader.Core;`
line entirely — the three value types now live in the same namespace as their consumer.

## Commit SHA lookup

```bash
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Pricing.MeritOrder/AvellanedaStoikov.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/QuotableRange.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/HittableRange.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/SideBias.cs
```
