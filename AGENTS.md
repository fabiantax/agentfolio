# AGENTS.md — operating manual for coding agents on AgentFolio

Password-gated → multi-tenant SaaS portfolio chat. .NET 10 / C# 14, Minimal APIs,
Clean Architecture vertical slices, EF Core 10 + SQLite (WAL/FTS5/sqlite-vec),
Microsoft Agent Framework on Microsoft.Extensions.AI. Specs in `specs/` (spec-kit),
constitution in `.specify/memory/constitution.md` (read both before feature work).

## Hard rules

1. **NO FALLBACKS. The primary feature must work.** A fallback that lets the app
   keep "working" (or tests keep passing) while the primary feature is broken or
   unconfigured is a bug, not resilience. When a dependency is missing: FAIL LOUDLY
   (503 + clear error, or throw at startup) — never silently substitute a fake,
   echo, in-memory stand-in, or degraded path. Silent fallbacks turn outages into
   fake demos (worst case for a product whose entire purpose is proving the owner
   doesn't bullshit).
   - Test doubles are NOT fallbacks: fakes live at trust boundaries in TEST projects
     only (fake IChatClient, fake IObjectStore, mocked GitHub HTTP). Production code
     paths must never contain them.
   - `if (env unset) use <lesser thing>` in Program.cs = a finding. Convert to
     fail-at-startup (required config) or fail-per-request (503 + named dependency).
2. **Cited or it didn't happen.** Every AI answer carries citations (constitution §2).
   An uncited answer is a bug; an empty-retrieval answer SAYS so.
3. **Real data in every env except unit tests** (constitution §3). Acceptance of any
   retrieval/ingest feature = hands-on real-data demo.
4. **Tenant boundary is server-derived.** Tenant ids come from the registry/session
   claims only — never from client input. Cross-tenant reads must fail closed
   (isolation matrix tests pin this).
5. **Gates before claims:** `dotnet build -warnaserror` (0 warnings) + `dotnet test`
   (unit + E2E) + `./scripts/arch-gate.sh`. CI = same, on the `strix-host` host lane
   (NEVER `container:` — no docker socket; `strix-main` has no live daemon).
   Coverage is collected and reported, never gated on the metric step.

## Known fallback debt (rule 1 hit-list — convert to fail-loudly)

- `ChatEndpoints`: keyed `nim` client falls back to an inline ECHO client when NIM
  env is unset. The E2E journey once passed against this echo — the exact fake-success
  this rule exists to kill. Fix: 503 `nim_not_configured` when env unset in production;
  the echo shape stays ONLY inside test DI overrides.
- `Program.cs` uploads: `IObjectStore` falls back to `InMemoryObjectStore` when S3 env
  unset. Fine for local dev with a loud startup warning; must be 503/refuse-writes in
  production mode (data would evaporate on restart — silently violating disposal promises).

## Layout

- `src/AgentFolio.Contracts` — pure DTOs + store interfaces (references nothing)
- `src/AgentFolio.Platform` — SQLite store, tenancy registry/shards, auth, storage, migrations
- `src/Slices/*` — vertical slices (Stars, Saves, Chat, Skills, Tenancy, Auth2, Uploads,
  Share); slices never reference each other, only Contracts
- `src/AgentFolio.Agents` — MAF wiring (NIM IChatClient factory, tier selector)
- `src/AgentFolio.Sandbox` — process-isolated extractor console app (untrusted bytes)
- `src/AgentFolio.Api` — Minimal API composition root; `src/AgentFolio.AppHost` — Aspire
- `tests/AgentFolio.UnitTests` (per-unit) + `tests/AgentFolio.E2E` (in-proc journeys,
  WebApplicationFactory — the fast Playwright alternative)
- `web/` — chat-core web component + vue/react/angular hosts; `extension/` — WXT LinkedIn harvester
- `scripts/arch-gate.sh` — Atlas clean-architecture gate (4 rules), see
  `docs/architecture/atlas-gate.md`

## Conventions that save hours

- csproj ASCII, NO BOM (AI lanes emit BOM'd XML that breaks MSBuild).
- xUnit: `Assert.Equal` is 2-arg; analyzers are errors (xUnit2010 etc.).
- Raw-string closers on their own line; positional records use ctor syntax.
- `TimeProvider` injected everywhere time matters (fake-clock tests).
- Skill to consult: `dotnet10-csharp14-modern` (language/BCL/algorithms/coverage).
- Deploy: Garage (not MinIO — archived) per `docs/deploy/k8s-notes.md`; secrets env-only.
