# Upstream

This repository contains source material donated from Arena under the BIFROST
copy-with-provenance rule. Every folder that contains Arena-donated content has its
own `UPSTREAM.md` capturing the original path and commit SHA. Files that were
authored fresh for BIFROST have no `UPSTREAM.md` annotation.

## Discipline

- Every donated file carries a folder-level `UPSTREAM.md` with the Arena commit SHA
  and the source path.
- GSD planning markers (`phase-NN`, `REQ-<CATEGORY>-<NN>`, `PLAN.md`, `.planning/`)
  are stripped before commit. Verified by `grep -rE 'phase[- ]\d{2}|REQ-[A-Z]+-[0-9]+|PLAN\.md|\.planning/' src/ contracts/ bigscreen/` returning zero matches.
- Adjustments to donated files are recorded in the folder-level `UPSTREAM.md` under a
  "Mutations applied" column.

## Arena reference

- Source repo: `/Users/jonathanjonler/RiderProjects/Arena/` (sibling working tree)
- Arena HEAD at initial donation (2026-04-22): `5f8da6072978a4693de7c7ec7f5ff9ea22181a0b`
- Per-file Arena commit SHAs are captured in each folder's local `UPSTREAM.md`.
