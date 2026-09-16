# Spec: multi-tenant SaaS

## Summary
Evolve AgentFolio from a single-owner password-gated portfolio chat into a multi-tenant SaaS: each user (candidate) gets their own tenant with OAuth GitHub sign-in, their own GitHub stars + LinkedIn saves ingestion, their own disposable upload bucket, and a public shareable portfolio chat URL. The original demo (Fabian's portfolio pre-seeded as the showcase tenant) remains the landing experience. The app demonstrates production-grade tenant isolation: a S3-style bucket with per-tenant prefixes as the hard data boundary, per-tenant SQLite indexes, and WASM-sandboxed parsing of untrusted uploads (issue #26 folds in).

## Goals
1. Candidate signs in with GitHub OAuth → tenant auto-provisioned with derived tenant id (opaque, never user-supplied) → their stars sync via their own OAuth token (no admin PAT), LinkedIn saves via per-tenant ingest token + existing Chrome extension.
2. Every visitor question is answered with citations from that tenant's stars+saves+uploads; Fabian's tenant = public showcase.
3. Visitors (recruiters/leads) of any tenant access via share URL + lightweight gate, chat with cited answers, can upload documents (e.g. a JD) into THEIR session-scoped disposable space that auto-expires (24h lifecycle).
4. Disposal guarantee: deleting a tenant = delete one bucket prefix + one per-tenant DB file. No cross-tenant rows anywhere. Verified by test.
5. FTS5 + sqlite-vec retrieval per tenant (per-tenant index.db opened on demand), WASM sandbox parses uploads, NIM answers, LFM2 browser tier unchanged.
6. Same single-writer SQLite constraints: one API pod per... (see NFR3 — tenant shards are independent so concurrency is per-tenant).

## User Stories
- US1 Candidate: As a candidate, I "Sign in with GitHub", my tenant is created, my stars sync automatically under my own token — no manual export.
- US2 Candidate: As a candidate, I install the extension, paste my tenant's ingest endpoint+token, and my LinkedIn saves flow in.
- US3 Candidate: As a candidate, I hit "Publish portfolio" → I get a share URL (portfolio.example.com/{slug}) to paste into applications.
- US4 Recruiter: As a recruiter, I open a share URL, optionally pass a lightweight gate, chat with cited answers about the candidate's stars/saves, and can upload a JD for tailoring — my upload lives 24h in my own disposable session space, then vanishes.
- US5 Candidate: As a candidate, I hit "Delete my account" → everything about me is gone in one prefix delete + DB drop, no residue.
- US6 Owner: As Fabian, my tenant is the public showcase (landing page defaults to it), pre-seeded, noindex'd vs tenant content.
- US7 Owner: As Fabian, I can see per-tenant usage (sync counts, chat counts, upload counts) for capacity + the demo story.
- US8 Recruiter: As a recruiter, I never see, nor can I construct a URL to, another tenant's data — tenant id is server-derived from session, never from the client.
- US8a Owner: As Fabian, a tenant who never returns still costs nothing: unreferenced tenant prefixes expire via lifecycle rule (30d idle) unless pinned.

## Requirements

### Functional
- FR1 Auth: GitHub OAuth (authorization code + PKCE, state nonce) sign-in creates/loads tenant. Session cookie (HttpOnly, Secure, SameSite=Lax). Admin bootstrap: first-tenant-as-owner or ADMIN_TOKEN env.
- FR2 Tenant provisioning: tenant_id = opaque server-generated (ULID), slug optional editable. Users table maps GitHub id → tenant. No user-supplied tenant id ever reaches storage paths.
- FR3 Tenant ingest: per-tenant GitHub sync uses the OAuth token granted at sign-in (scope=read:user,user) or a per-tenant PAT stored per-tenant; per-tenant LinkedIn ingest token (generated on demand, revocable).
- FR4 Share URL: GET /{slug} renders the chat for that tenant (public read of that tenant's content only), optional per-share password gate (tenant-configurable), noindex on all tenant pages.
- FR4a Uploads (visitor sessions): authenticated chat session can upload files (pdf/txt/md/docx, ≤10MB, content-sniffed, extension untrusted) to tenants/{tid}/sessions/{sid}/; extraction via WASM sandbox (issue #26); extracted text + vec index into the session's disposable index; 24h lifecycle auto-expiry + explicit DELETE /session/data; visitor cannot write outside their session prefix (server-derived paths only).
- FR4b Uploads (candidate/owner): same mechanism, but into tenants/{tid}/uploads/ with pin option (no auto-expiry) — e.g. candidate pins their CV.
- FR5 Retrieval: per-tenant FTS5 + vec0 index (tenants/{tid}/index.db, open-on-demand via TenantIndexRegistry with LRU eviction), hybrid query pre-generation; session uploads join the same hybrid query for the duration of the session.
- FR6 Chat: as spec-001 FR2 (SSE stream, persistence) but tenant-scoped: conversation + citations can only reference tenant content + session uploads.
- FR7 Skills view: per-tenant derived taxonomy (existing derivation logic, scoped).
- FR8 Event sourcing: unchanged (events append per tenant shard — see NFR3); rebuild command per tenant.
- FR9 Admin: usage view (US7), tenant list, slug edit, disable tenant, revoke ingest token. Minimal: admin token + JSON endpoints.
- FR10 Disposal: DELETE /api/tenant (owner-only) = bucket prefix delete + tenant DB drop + session/user rows purge; response includes a receipt (bytes freed, counts); test asserts zero residue (no row in any table referencing the tenant, no prefix in bucket).
- FR11 LFM2 browser tier: unchanged (WebGPU detect → local simple replies, server fallback).
- FR12 NIM server tier: unchanged, now tenant-scoped RAG.

### Non-Functional
- NFR1 Isolation: the bucket prefix IS the tenant boundary; per-tenant SQLite files are physically separate files; no query ever joins across tenants. Isolation test: seeded two tenants, attempt cross-tenant read via every public route (chat, retrieval, skills, share URL with wrong slug, upload path traversal) → all denied.
- NFR2 Disposable data: tenant delete = O(prefix) deletion; session uploads auto-expire 24h; idle tenants auto-expire 30d unless pinned. Lifecycle rules declarative (bucket IaC) + enforcer job fallback.
- NFR2a Split data domains: operational store (global.db: users, tenants, sessions, shares) on PVC; tenant shards (tenants/{tid}/data.db + index.db) and uploads in the bucket. Operational SQLite NEVER goes in the bucket (random-writes vs object storage anti-pattern).
- NFR2b Upload security: size/MIME gate, content sniffing, filename sanitization, path-traversal proof (server-derived paths), virus-scan hook (no-op default, ClamAV optional), WASM-sandboxed parsing.
- NFR2c Rate limits: per-tenant + per-session rate limits (chat, upload, sync) — token bucket, per identity.
- NFR3 Concurrency: shards are independent → per-tenant single-writer constraint only; global.db holds only hot identity/session state. Future: shard-level WAL + busy timeout... (target: no cross-tenant lock contention).
- NFR4 Cost ceiling: one k3s node demo (Garage single-node, replication 1, acceptable for demo; prod = Hetzner Objects. MinIO RETIRED: upstream archived Feb 2026 — replaced by Garage (Rust, Deuxfleurs); Silo (pgsty MinIO fork) is the parity fallback if S3 Object Lock/lifecycle-server-side is ever needed. Garage lacks server-side lifecycle rules — the UploadExpiryEnforcer BackgroundService is the expiry mechanism of record (not a fallback).
- NFR5 Browser model unchanged (LFM2-350M q4 WebGPU, CDN-loaded, no build-time download).
- NFR6 Privacy: recruiters' uploads auto-expire; question logs per-tenant; GDPR delete = FR10.
- NFR7 Performance: chat p95 < 10s with retrieval (spec-001 goal holds); index open < 50ms p95 (tenant LRU).
### Migration
- MIG1 Existing single-owner data (stars/saves/events/skills on main PVC SQLite) becomes tenant "showcase" — one-off migration script, verified by tests + parity run.
- MIG2 Existing password gate retires: the shared password feature stays ONLY as per-share optional gate (FR4).
- MIG3 Chrome extension gains per-tenant endpoint+token config (was global INGEST_TOKEN).

### Open Questions
- OQ1 Slug collisions & vanity URLs: first-come? editable? (lean: editable, collision-checked, no squatting rule for v1.1)
- OQ2 GitHub OAuth app ownership: user-owned OAuth app per deployment vs hosted multi-deployment app (lean: per-deployment env GITHUB_CLIENT_ID/SECRET)
- OQ3 Visitor identity: anonymous session vs email-lightweight (lean: anonymous session cookie + optional share password)
- OQ4 Do recruiter uploads feed the tenant's index? (lean: session-scoped only, 24h, never persisted into tenant index — upload answers cite the upload only)
- OQ4a Does the candidate's pinned CV enter the index? (lean: yes, that's the point of pinning)
- OQ5 Brotli/compression of index shards? (lean: no, over-engineering for v1.1)
- OQ6 RESOLVED: Garage single-node replication-1 for demo (documented risk); server-side lifecycle not available — enforcer owns expiry
- OH7 What happens to the WASM sandbox (issue #26) scope now? (lean: unchanged, it parses uploads — folds INTO this spec as FR4a's implementation detail)
- OQ8 If GitHub OAuth token expires and user doesn't return, does idle-expiry delete their stars? (lean: yes — that's the disposable default; pin to preserve)

### Change Log
- 2026-09-15: initial draft (spec-kit format, follows 001 conventions)
