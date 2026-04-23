# Upstream — tests/Bifrost.Quoter.Tests/Pricing/

## Files

| Donated path (this folder)   | Original path (Arena)                                                            | Arena commit SHA                           | Mutations applied                                                                                                                                                                                                                                                          |
|------------------------------|----------------------------------------------------------------------------------|--------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `AvellanedaStoikovTests.cs`  | `tests/trader/ArenaTrader.Pricing.MeritOrder.Tests/AvellanedaStoikovTests.cs`    | `7983c32a97ca94dd02c89899ce4d1940ad625284` | Namespace `ArenaTrader.Pricing.MeritOrder.Tests` -> `Bifrost.Quoter.Tests.Pricing`. Removed `using ArenaTrader.Core;` (co-donated value types live in `Bifrost.Quoter.Pricing`). Replaced FluentAssertions assertions with plain xUnit `Assert.*` per BIFROST test convention. Dropped four `MeritOrderPricingConfig` / `AsParameters` tests whose dependencies are out of Phase 03 donation scope. |

## Divergence rationale

Two deliberate divergences from a strictly verbatim port:

1. **FluentAssertions -> plain xUnit `Assert`.** BIFROST test projects do not pin
   `FluentAssertions` (Phase 02 dropped Arena's pin in `Directory.Packages.props`).
   Each ported assertion was rewritten with the equivalent `Assert.*` shape:
   `Should().Be(x)` -> `Assert.Equal(x, actual)`,
   `Should().BeApproximately(x, t)` -> `Assert.Equal(x, actual, t)`,
   `Should().BeGreaterThan(x)` -> `Assert.True(actual > x)`,
   `Should().BeLessThan(x)` -> `Assert.True(actual < x)`,
   `Should().Be<Enum>` -> `Assert.Equal(<Enum>, actual)`.

2. **Dropped 4 config tests.** Arena's
   `Config_DefaultParams_CreatesSuccessfully`,
   `Config_EmptyPerArea_GetForAreaReturnsDefault`,
   `Config_PopulatedPerArea_GetForAreaReturnsConfiguredValue`,
   `Config_HittableMultiplierBelowOne_Throws`
   exercise `MeritOrderPricingConfig`, `AsParameters`, and `DeliveryArea` --
   none of which are in the Phase 03 donation surface (the quoter consumes
   pure A-S math only; per-area pricing-config types are an Arena-strategy
   concern, not a BIFROST quoter-NPC concern). Per the math-only triage rule
   for ported test files, these were dropped rather than re-implemented.

The 12 retained tests cover every public method on
`Bifrost.Quoter.Pricing.AvellanedaStoikov` and every co-donated value type
(`QuotableRange`, `HittableRange`, `SideBias`).

## Commit SHA lookup

```bash
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- tests/trader/ArenaTrader.Pricing.MeritOrder.Tests/AvellanedaStoikovTests.cs
```
