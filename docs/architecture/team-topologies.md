# AgentFolio — Team Topologies Map

Four team types, mapped to solution structure and fab-swarm task routing.

## Platform team — `src/AgentFolio.Platform`
Owns: auth (Argon2id password gate, cookie sessions, rate limiting), the SQLite store (EF Core 10 + FTS5 + sqlite-vec + graph CTEs), event store plumbing, background sync host (GitHub stars poller), deployment (Aspire AppHost + Helm/k3s).
Serves: all stream-aligned slices. Contracts: `IEntityStore` role-narrow interfaces, auth middleware, DI registrations.
Routing: platform tasks are `code`/`architecture`, medium priority, must land early (waves 1-2).

## Stream-Aligned team — `src/Slices/*` (one folder per capability)
Slices:
- `Chat` — conversation UI API, streaming responses, citations (owns the recruiter experience end-to-end)
- `Stars` — GitHub stars ingestion surface, browsing endpoints, sync status
- `Saves` — LinkedIn saved-posts ingestion surface (receives extension payloads), browsing endpoints
- `Skills` — skills/career claims derived from stars+saves, the "proof" view
Rule: a slice never references another slice. Cross-slice reads go through Platform contracts or events.
Routing: `code` tasks, highest business priority, wave 2+ after platform contracts.

## Enabling team — `.specify/`, CI, templates, this map
Owns: spec-kit workflow (constitution/spec/plan/tasks), Gitea CI pipeline, PR templates, the web-component chat wrapper examples, docs.
Routing: `architecture`/`review` tasks; frequently pairs with other teams (reviews).

## Complicated-Subsystem team — `src/AgentFolio.Agents`
Owns: Microsoft Agent Framework workflows (RAG chat agent, citation middleware, re-index agent), embedding pipeline, model tiering (LFM2 browser client vs NVIDIA NIM server client), the `IChatClient` selection logic.
Serves: Chat slice (its only consumer). Must not know about HTTP, auth, or stores beyond injected ports.
Routing: `architecture` then `code`; hardest units, strongest models, wave 2-3.

## Frontend — `web/` (framework-agnostic core)
- `web/chat-core`: framework-agnostic chat Web Component (Lit or vanilla CE), talks REST/SSE to the API, lazy-loads LFM2 via transformers.js on WebGPU with graceful NIM fallback.
- `web/hosts/vue`, `web/hosts/react`, `web/hosts/angular`: thin demo hosts embedding the component. Each host also demonstrates framework-specific wrapping (Vue prop binding, React hook, Angular wrapper) — that's part of the portfolio story.
- `extension/`: WXT + TypeScript Chrome MV3 extension (pattern: localscout clipper) — scrapes linkedin.com/my-items/saved-posts, queues offline, POSTs to /api/ingest/saves with an ingest token.
Routing: `code`, parallel-friendly, wave 2-3.

## Interaction modes
- Platform × Stream-Aligned: as-a-service (Platform ships contracts; slices self-serve).
- Enabling × everyone: facilitating (templates, CI, reviews).
- Complicated-Subsystem × Chat: X-as-a-service with embedded expertise (Agents ships an agent factory; Chat just calls it).
