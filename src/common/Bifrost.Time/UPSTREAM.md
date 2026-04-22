# Upstream — src/common/Bifrost.Time/

## Files

| Donated path (this folder)  | Original path (Arena)                                | Arena commit SHA                             | Mutations applied                                                                                                                                    |
|-----------------------------|------------------------------------------------------|----------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------|
| `IClock.cs`                 | `src/exchange/Exchange.Application/IClock.cs`        | `040069112d7eed0016ec849eaf838d033f173358`   | Namespace `Exchange.Application` → `Bifrost.Time`. Replaced property `UtcNow { get; }` with method `GetUtcNow()` to mirror TimeProvider's API shape. |
| `SystemClock.cs`            | `src/exchange/Exchange.Application/SystemClock.cs`   | `040069112d7eed0016ec849eaf838d033f173358`   | Namespace `Exchange.Application` → `Bifrost.Time`. Body `DateTimeOffset.UtcNow` → `TimeProvider.System.GetUtcNow()` (see rationale below).           |

## Divergence rationale

BIFROST's `SystemClock.GetUtcNow()` delegates to `TimeProvider.System.GetUtcNow()` rather than
returning `DateTimeOffset.UtcNow` directly. Three reasons:

1. `TimeProvider.System` is the alternative named in `build/BannedSymbols.txt`. If the approved
   alternative does not use TimeProvider, the error messages lie.
2. Tests substitute `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`) via a thin
   `TestClock : IClock` adapter — trivial with TimeProvider, impossible with raw UtcNow.
3. If a future phase bans `DateTimeOffset.UtcNow`, this file is already compliant.

## Commit SHA lookup

```bash
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/exchange/Exchange.Application/IClock.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/exchange/Exchange.Application/SystemClock.cs
```

Re-run at implementation time; overwrite the SHAs above if Arena has advanced.
