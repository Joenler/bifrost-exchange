# Upstream — bigscreen/

## Files

| Donated path (this folder) | Original path (Arena)                | Arena commit SHA                             | Mutations applied                                                                                                                                                                                                         |
| -------------------------- | ------------------------------------ | -------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `package.json`             | `src/dashboard/package.json`         | `039e72b31854e67c71e8f72afff001ef25178e88`   | Name `arena-dashboard` → `bifrost-bigscreen`. Dropped the charting / table / state / layout deps that belong to the real spectator UI (`lightweight-charts`, `echarts`, `echarts-for-react`, `@tanstack/react-query`, `@tanstack/react-table`, `zustand`, `react-resizable-panels`). `preview` script gained `--host 0.0.0.0 --port 4173`. |
| `tsconfig.json`            | `src/dashboard/tsconfig.json`        | `95d2a1990f0710c99d8b711362b7d3c44497ac36`   | verbatim                                                                                                                                                                                                                  |
| `tsconfig.app.json`        | `src/dashboard/tsconfig.app.json`    | `95d2a1990f0710c99d8b711362b7d3c44497ac36`   | verbatim                                                                                                                                                                                                                  |
| `tsconfig.node.json`       | `src/dashboard/tsconfig.node.json`   | `95d2a1990f0710c99d8b711362b7d3c44497ac36`   | verbatim                                                                                                                                                                                                                  |
| `vite.config.ts`           | `src/dashboard/vite.config.ts`       | `23b081efbc098de765028c1e7f1710d80e0fa256`   | Dropped `server.proxy` block — no backend in this scaffold.                                                                                                                                                                |
| `vitest.config.ts`         | `src/dashboard/vitest.config.ts`     | `95d2a1990f0710c99d8b711362b7d3c44497ac36`   | Added `setupFiles: ['./src/setupTests.ts']` so `@testing-library/jest-dom/vitest` matchers register.                                                                                                                       |
| `eslint.config.js`         | `src/dashboard/eslint.config.js`     | `039e72b31854e67c71e8f72afff001ef25178e88`   | verbatim (preserves `react-hooks/rules-of-hooks: "error"`).                                                                                                                                                                |
| `index.html`               | `src/dashboard/index.html`           | `95d2a1990f0710c99d8b711362b7d3c44497ac36`   | `<title>` swapped to `BIFROST — Spectator Dashboard`.                                                                                                                                                                     |

## Dependencies deliberately dropped

Seven dependencies from Arena's dashboard `package.json` are intentionally absent here. They
are charting, table, state-management, and layout libraries that belong to the real
spectator UI (not this placeholder scaffold) and keep `npm ci` fast in CI:

- `@tanstack/react-query` — WebSocket/query integration.
- `@tanstack/react-table` — leaderboard table.
- `lightweight-charts` — orderbook + price time-series.
- `echarts` + `echarts-for-react` — leaderboard + event histograms.
- `zustand` — client-side store.
- `react-resizable-panels` — multi-instrument grid layout.

When the real UI lands, these are re-added with pins per the tech-stack contract.

## Commit SHA lookup

```bash
for f in package.json tsconfig.json tsconfig.app.json tsconfig.node.json vite.config.ts vitest.config.ts eslint.config.js index.html; do
    sha=$(git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/dashboard/$f)
    echo "$f $sha"
done
```

Re-run if Arena rebases; refresh the SHAs in the table above.
