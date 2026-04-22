# Upstream — build/

## Files

| Donated path (this folder) | Original path (Arena)          | Arena commit SHA                                          | Mutations applied |
|---------------------------|--------------------------------|-----------------------------------------------------------|-------------------|
| `BannedSymbols.txt`       | `build/BannedSymbols.txt`      | `44852b4042b6fa610cf64052c02826724d1093ef` (syntax only)  | Replaced Arena's TIMER/GUARD entries with BIFROST determinism bans (Random.Shared, DateTime.UtcNow). Arena's entries contain planning markers that would violate the GSD-marker scrub assertion; BIFROST entries are authored fresh and reference `Bifrost.Time.IClock` as the approved alternative. |

## Commit SHA lookup

```bash
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- build/BannedSymbols.txt
```

Re-run if the executor finds Arena has advanced since 2026-04-22; overwrite the SHA above with the returned value.
