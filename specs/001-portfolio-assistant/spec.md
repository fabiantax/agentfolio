# Spec: portfolio-assistant

## Summary
Password-protected portfolio chat application. Recruiters and potential client leads chat with an AI assistant that answers questions about Fabian's GitHub stars and LinkedIn saved posts, with citations. Answers demonstrate real expertise; the app itself demonstrates modern C#/.NET craftsmanship.

## Goals
1. Recruiter asks "what Rust projects has Fabian starred?" / "which AI agent frameworks saved?" → cited, grounded answer in <10s.
2. In-browser LFM2-350M (WebGPU) answers simple/cheap queries client-side; NVIDIA NIM handles server-side RAG when retrieval is needed or WebGPU is absent.
3. Chrome extension harvests LinkedIn saved posts from linkedin.com/my-items/saved-posts/ (localscout-clipper pattern) — user never manually exports.
4. GitHub stars sync via PAT-backed poller.
5. Deployed on Hetzner k3s via Aspire → Helm, password-gated on the public internet.

## User Stories
- US1 Recruiters: As a recruiter, I enter the shared password, then chat; every answer lists clickable sources (repo URL, LinkedIn post title+URL where available), so I trust the answer.
- US2 Recruiters: As a recruiter with a weak laptop, the chat still works (server model fallback) — I'm never blocked by my browser.
- US3 Owner: As Fabian, I click the extension on my saved-posts page and my saves flow into AgentFolio automatically (deduped).
- US4 Owner: As Fabian, my stars sync nightly (and on-demand) without me touching anything.
- US5 Leads: As a potential client, I see a curated Skills view derived from my stars+saves — proof I work with these technologies.
- US6 Owner: As Fabian, I can see what leads asked (anonymized question log) to tune the demo.

## Requirements

### Functional
- FR1 Password gate: single shared password (Argon2id), cookie session, rate-limited (5/min, lockout 15min), noindex, HTTPS cookies.
- FR2 Chat: streaming responses (SSE), full conversation persistence, citations rendered as links in the answer UI.
- FR3 Retrieval: hybrid — FTS5 keyword + sqlite-vec cosine on embeddings + graph expansion (stars↔topics↔skills edges) before generation.
- FR4 Skills view: derived taxonomy from starred repo topics/languages and saved-post keywords; browseable.
- FR5 GitHub sync: PAT, list stars (paginated REST), store repo metadata + README excerpt + topics; nightly + manual trigger.
- FR6 LinkedIn ingest: extension POSTs JSON batches with an ingest token; upsert on canonical post URL; offline queue.
- FR7 Event-lite sourcing: every state change appends an event row before projection update; rebuild command exists and is tested.
- FR8 Model tiering: route simple queries to LFM2 in-browser when WebGPU present; NIM server-side otherwise or when retrieval required. Browser never receives PAT/ingest secrets.
- FR9 Query log: anonymized (IP hashed) question log with per-question latency and model tier used.

### Non-functional
- NFR1 <10s first token under normal load; <3s for in-browser tier.
- NFR2 Single SQLite file, WAL mode; rebuild-from-events <30s at 10k items.
- NFR3 Total RAM <512MB server-side excluding models.
- NFR4 99% uptime target on Hetzner VM; single-writer SQLite (replicas=1), PVC-backed.
- NFR5 All answers carry citations; citation-less answers are a defect (constitution §2).

## Edge cases
- Saved post deleted/unavailable: keep entity, mark stale, exclude from retrieval but keep in browsing.
- WebGPU unavailable → seamless NIM fallback with a small "server model" badge.
- Embedding model failure → degrade to FTS-only search, answer with fewer citations, say so.
- Zero stars/saves (fresh install) → assistant explains sync setup instead of hallucinating.
- Rate limit lockout → 429 page with retry-after.
- Extension hits rate limit → queue persists, backoff, flush on reconnect.

## Ten guiding principles (from constitution)
1. Purpose before tech — demo-first scoping
2. Cited, grounded answers — no invention
3. Real data always — fixtures only in unit tests
3. Security is a feature demo
5. Cheap single-SQLite store; vertical slices
6. Event-lite append-only trail
7. One IChatClient abstraction, two runtimes
8. Build gate sacred (warnaserror + tests + Gitea CI)
9. Team Topologies boundaries are code boundaries
10. C# 14/.NET 10, boring and idiomatic

## Success Metrics
- Recruiter demo (hands-on, Tailscale) with real data: PASSED before any merge to main of the chat feature.
- p95 first-token <10s server tier, <3s browser tier (measured in query log).
- 100% of sampled answers carry ≥1 citation.
- Ingest-to-searchable latency <60s.
- Infra cost: $0 incremental (existing Hetzner VM).

## API surface (minimal, v1)
- POST /auth/login {password} → cookie
- POST /chat {conversationId?, message} → SSE stream (citations in final event)
- GET /stars?cursor=, GET /saves?cursor=, GET /skills
- POST /ingest/saves (token) ← extension
- POST /admin/sync/stars (auth) → 202
- GET /health
