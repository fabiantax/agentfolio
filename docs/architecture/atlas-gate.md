# Clean Architecture Gate (Atlas)

The repo enforces slice isolation continuously via `scripts/arch-gate.sh`, which
runs 4 Cypher rules over an Atlas (graphfusion) scan of the codebase.

## The 4 rules

1. **slice-isolation** — no project reference from one `src/Slices/*` project to
   another. Slices communicate through `AgentFolio.Contracts` only.
2. **agents-no-platform** — `AgentFolio.Agents` must not reference
   `AgentFolio.Platform` internals (AI layer stays a consumer, not a store peer).
3. **contracts-purity** — `AgentFolio.Contracts` references nothing (pure DTOs +
   store interfaces; zero dependencies by design).
4. **ef-encapsulation** — EF Core/SQLite types must not leak outside
   `AgentFolio.Platform` (slices see `IEventStore`/`IItemStore`, never DbContext).

## Running it

```sh
./scripts/arch-gate.sh
```

Requirements:
- `atlas` binary at `~/.local/bin/atlas` (285MB — host-only, not fetched in CI)
- A clean tree: stale `bin/``obj/` from prior builds produce phantom edges —
  the script removes them before scanning (known atlas graphfusion-query quirk:
  scan path and query path must match exactly or the store name differs).

Output: `arch-gate: all clean-architecture rules pass` (exit 0) or the failing
rule with the offending edges.

## Where it runs

| Where | Status |
|---|---|
| Parent-side verification (every PR) | ✅ every merge tonight ran it |
| Local dev | ✅ `./scripts/arch-gate.sh` |
| CI (`.gitea/workflows/ci.yml`) | ⚠️ runs when `atlas` is present on the runner; **skips loudly with a `::warning::` annotation otherwise** (binary too large to fetch per-run; never silently green) |
| Nightly | planned (hardening backlog #16) |

## Why graph-based

File-name and namespace conventions rot; the Atlas scan grounds the gate in the
actual reference graph (44+ nodes at last scan). Violations it has caught for
real: a planted `Illegal.cs` surviving a branch switch, and template stub
classes referencing a placeholder namespace.

The companion skill lives at `~/.hermes/skills/software-development/atlas-clean-arch-gate/`.
