# CLAUDE.md — bifrost-exchange

Engineering-rigor rules for this repository. Inherited in spirit from Arena's
CLAUDE.md but authored fresh to keep internal organizer-side planning markers
out of the source tree.

## .NET

- Target framework: `net10.0`. Nullable enabled. `TreatWarningsAsErrors=true`.
  Implicit usings. Allman braces. 4-space indent. xUnit v3.
- Every csproj inherits these from root `Directory.Build.props` — do not override.
- Centralized package versions live in `Directory.Packages.props`
  (`ManagePackageVersionsCentrally=true`). Services reference `<PackageReference>`
  entries without per-csproj versions.

## Determinism

- **Never** `System.Random.Shared`. Use `new Random(scenarioSeed)` seeded from
  the scenario seed passed through DI.
- **Never** un-injected `System.DateTime.UtcNow`. Use `Bifrost.Time.IClock` (see
  `src/common/Bifrost.Time/`) which wraps `TimeProvider.System`. In tests,
  substitute `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`).
- `build/BannedSymbols.txt` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` enforce
  these bans at build time. Violations become build errors via
  `TreatWarningsAsErrors=true`.

## Concurrency

- The matching engine is single-writer; no locks.
- Gateway per-team state uses `Monitor` locks with documented lock ordering.
- **No** `ConcurrentDictionary<,>` compound (read-modify-write) operations on
  scoring-relevant state: `GetOrAdd` with side-effecting factory, `TryGetValue`
  followed by `dict[key] = ...` mutation, `AddOrUpdate` with mutating updater.
  Enforced by `.github/scripts/lint-concurrent-dictionary.sh` in CI.
- Escape valve: a line immediately preceding a flagged call with
  `// bifrost-lint: compound-ok — <reason>` is exempted (reason captured in CI log).

## Wire-format cross-check

- Whenever anything crosses a language or process boundary (protobuf over gRPC
  especially), verify both sides in the same change set. Round-trip CI tests in
  the `contracts/` slot are the enforcement surface.
- Do not introduce a parallel Python representation of wire types (e.g.
  Pydantic mirror of a protobuf message). Generated protobuf classes are the
  single source of truth on the Python side.

## TypeScript (bigscreen)

- Build: `tsc -b && vite build`. **Never** `tsc --noEmit` — `tsc -b` is stricter
  and catches index-access narrowing and `useRef` initial-value issues that
  `--noEmit` silently passes.
- React hooks rule-of-hooks is `error` (not warning). See
  `bigscreen/eslint.config.js`.
- TanStack Query v5 + Zustand v5 + `lightweight-charts` v5 (prices) + `echarts`
  v6 (leaderboard). Do not introduce `Recharts`, `visx`, or `socket.io`.

## Git hygiene

- Commits always have title + body (never single-line).
- No `Co-Authored-By:` or `Signed-off-by:` trailers.
- Per-file staging only (no `git add -A` / `git add .`).

## Copy-with-provenance

- Every file or folder donated from a sibling codebase (Arena) carries a
  folder-level `UPSTREAM.md` recording the source commit SHA and path.
- Donated source is scrubbed of organizer-side planning markers before commit.
  The repo-wide grep fence `grep -rE 'phase[- ][0-9]{2}|REQ-[A-Z]+-[0-9]+'`
  over `src/`, `contracts/`, and `bigscreen/` must return zero matches.
