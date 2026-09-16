# Spec: architecture-lab — one module, N architectures, measured

## Summary
The SAME module implemented N times in different architectural styles, behind one
behavioral contract, with objective measurements (LOC, coupling via Atlas graph,
change-amplification scenarios, test/coverage parity). Purpose: prove architectural
judgment to recruiters/leads ("I know WHEN each style pays"), and settle internal
debates with numbers instead of vibes.

## The module: Stars
Commands: SyncStars (external GitHub fetch, pagination, rate-limit backoff).
Queries: ListStars (cursor paging), SkillsTaxonomy (derivation).
Events: StarsSynced (append-only log).
External dep: IGitHubStarsClient. Storage: SQLite.
Why: small (fits in one screen per style), but exercises commands, queries, events,
an external system, and a projection — enough surface for style differences to show.

## The contract (identical for every variant)
- HTTP: POST /stars/sync -> {fetched,upserted,unchanged}; GET /stars?cursor&limit
- Same DTOs (StarredRepoRecord), same DB schema (stars + events), same wire format
- Same black-box tests (copied per variant, zero test-logic changes)
- Change scenarios (the real measurement): S1 add field to Star, S2 add business rule
  (dedupe by name), S3 second read model (stars-per-language). Count files touched.

## Variants
1. vertical-slice (EXISTS — baseline; current src/Slices/Stars)
2. layered — BLL/DAL/UI projects, service interfaces, DTO mappers
3. cqrs — command/query handlers (hand-rolled dispatcher), separate read/write models
4. cqrs-es — full event sourcing: aggregate replay, projection rebuild
5. onion — Clean Architecture: Domain/Application/Infrastructure/Presentation, DI inversions
6. uow-repo — Unit-of-Work + generic Repository over EF, services orchestrate
7. modular-monolith — module contracts + internal implementations, module-boundary tests
8. eda — in-proc bus, async commands, eventually-consistent read side
9. functional-core — pure decision functions, imperative shell at edges
10. team-topologies — not code: an OWNERSHIP map showing how 2-4 team shapes
    (stream-aligned/platform/enabling) would split the SAME module + interaction modes

## Measurements (per variant, table in README)
LOC / files / projects; Atlas coupling metrics (fan-in/out, cycles); build+test time;
coverage; S1-S3 files-touched (change amplification); a 3-line "when to use" verdict.

## Non-goals
Performance benchmarks (IO-dominated, meaningless here). UI. Auth.
