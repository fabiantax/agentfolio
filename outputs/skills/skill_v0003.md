---
name: csharp-solid-coupling
description: "C# SOLID/DRY/KISS rules for loosely-coupled systems."
version: 1.0.0
metadata:
  hermes:
    tags: [csharp, dotnet, solid, dry, kiss, clean-architecture, coupling, code-review]
---

# C# SOLID / DRY / KISS — loosely-coupled systems

Rules for authoring AND reviewing C#. Written for any agent (generic or C#-specialized) — load on any C# write/review task.

## SOLID, concretely
- **S**: one reason to change per type. A service that syncs GitHub stars AND rate-limits AND logs has 3 reasons — split.
- **O**: add behavior by adding a class (new IChatClient impl), never by editing a switch that grows. Strategy/function injection over enum branching.
- **L**: subtypes must be substitutable — enforced by parity tests (same contract test suite runs against every impl). If a test needs `if (impl is SqliteStore)`, the abstraction is lying.
- **I**: role-narrow interfaces (IEventStore, IItemStore) beats one god-IService. Consumers depend only on methods they call.
- **D**: business logic depends on ports (interfaces you own in Contracts/), never on EF/Octokit/Http types. DI composes at the composition root (Program.cs) ONLY — `new` for a dependency inside a handler is the smell.

## Coupling rules (loose coupling focus)
1. **A→B concrete reference = tight.** Cross-slice/cross-team calls go through: (a) a shared Contracts type, (b) an injected port, or (c) an event. Never `using Slices.Chat` from Slices.Stars.
2. **Events decouple what from how**: publisher appends domain event, doesn't know subscribers. Event-lite sourcing: append event BEFORE projection update, same transaction.
3. **DI lifetimes are architecture**: Singleton holds ONLY immutable state (FrozenDictionary, clients); Scoped = DbContext per request; Transient = stateless workers. Captive dependency (Singleton ctor-injects Scoped) = bug, caught at first resolve of a different scope.
4. **Configuration via IOptions<T> + ValidateDataAnnotations()** — fail fast at startup, never `configuration["key"]` scattered in logic.
5. **Primative-obsession boundary**: domain types (CanonicalUrl, RepoId records) at boundaries; strings only inside infra.

## DRY without damage
- Duplicate CODE smells; duplicate KNOWLEDGE must be single-sourced. But: two slices with similar-looking DTOs that evolve for different reasons are NOT duplication — premature dedup couples them (wrong abstraction > duplication). Rule: dedup on the 3rd occurrence, with a name that is not a conjunction ('UserAndOrderService').
- Extract to Contracts only when 2+ consumers exist TODAY.

## KISS / YAGNI
- Records + pattern matching over inheritance trees. `sealed` by default; `virtual` needs a reason (perf + design).
- No interface for a single impl 'for later' (YAGLI) unless it's a port crossing a layer boundary or needs test substitution — if you never fake it in tests, the interface is ceremony.
- Minimal APIs for endpoints; a class only when logic outgrows the lambda.
- Async: no async-over-sync (.Result/.Wait()), no sync-over-async. `ValueTask` only on hot paths with measurable benefit.

## Review checklist (any agent can run this)
- [ ] `new` of any external client outside Program.cs/tests?
- [ ] using directive crossing a slice/team boundary?
- [ ] interface with 1 impl AND 0 fakes?
- [ ] static mutable state? (singleton via static, not DI)
- [ ] method >40 lines / file >400 (warn) / >800 (block)?
- [ ] TODO/stub/placeholder in committed code?
- [ ] domain logic in controller/handler that belongs in the domain type?
- [ ] thrown exceptions used for control flow between layers you own?

## What to avoid (biting patterns)
- Service-locator (IServiceProvider injected and resolved mid-logic) — hides the dependency graph.
- 'Manager'/'Helper'/'Utils' types — name the responsibility or split.
- Interface-per-class dogma + impl-per-interface mock tests = testing the mock, not the contract.
- Optional params (>3) — a parameter object is clearer and extensible (OCP).
- Base-class hooks for cross-cutting concerns — use middleware/decorators (DI) instead.
