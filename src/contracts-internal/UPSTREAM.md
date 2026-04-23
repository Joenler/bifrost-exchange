# UPSTREAM

**Source repo:** Arena (`/Users/jonathanjonler/RiderProjects/Arena/`)
**Source commit:** `5f8da6072978a4693de7c7ec7f5ff9ea22181a0b`
**Source date:** 2026-04-22
**Source path:** `src/contracts/Contracts/`

## Copy-with-provenance notes

- Entire folder copied verbatim.
- No GSD markers in source (pre-lint-scrubbed; `ci-lint-fence-negative` verifies).
- Namespace converted: `Contracts.*` -> `Bifrost.Contracts.Internal.*`.
- csproj renamed: `Contracts.csproj` -> `Bifrost.Contracts.Internal.csproj`.
- Dead-code DTOs retained (`PublicOrderStatsEvent`, `TraderMetricsSnapshot`, `LifecycleEventDto`, etc.) per YAGNI-delete-later posture; downstream plans remove unused entries once the exchange fork is in place.
- BIFROST-specific additions: `Shared/QuantityScale.cs` (decimal<->int64_ticks helper).
