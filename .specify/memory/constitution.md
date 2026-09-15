# AgentFolio Constitution

Non-negotiable principles. Every spec, plan, task, and PR must comply. Conflicts resolve in favor of the earlier-numbered principle.

## 1. Purpose before tech
AgentFolio exists to convince a recruiter or client lead, in under 5 minutes of chatting, that Fabian's claimed expertise is real. Every feature must serve that demo. If a feature does not make the demo more credible, it is out of scope for v1.

## 2. Answers must be grounded and cited
Every AI answer MUST cite its sources (repo link / saved-post link). An uncited answer is a bug. If retrieval finds nothing, the assistant says so — it never invents stars or posts.

## 3. Real data, always
The app runs against Fabian's REAL GitHub stars and REAL LinkedIn saves in every environment, including local dev. Fixtures exist only for unit tests. Acceptance for any retrieval feature = hands-on demo with real data.

## 4. Security is a feature demo
The app is internet-exposed. Password gate (Argon2id), rate limiting, HTTPS-only cookies, noindex headers, and secret hygiene are v1 acceptance criteria, not hardening tickets. No API key or PAT may ever reach a browser bundle or a public repo.

## 5. Cheap to run, cheap to change
Single SQLite file (WAL) is the only datastore. No external DB services in v1. Total infra cost target: fits on an existing Hetzner VM alongside other workloads. Vertical slices: one feature = one folder; no cross-slice references except through the contracts project.

## 6. Honest event trail
State changes are recorded as append-only events before the projection is updated (event-lite). Projections can be rebuilt from events. No event, no state change.

## 7. One AI abstraction, two runtimes
All model access goes through IChatClient (Microsoft.Extensions.AI). LFM2-350M (WebGPU, in-browser) and NVIDIA NIM (server) are interchangeable behind it. Feature code never knows which model answered.

## 8. Build gate is sacred
`dotnet build -warnaserror` + per-slice tests green before every merge. CI on Gitea runs the same gates as local. Never merge red. Never lift branch protection.

## 9. Team Topologies boundaries are code boundaries
Stream-aligned slices never reference each other. Platform owns auth/store/ingest plumbing. Enabling owns specs/CI/templates. Complicated-subsystem owns the agent/RAG engine. A PR that crosses a boundary it shouldn't must be rejected in review, not refactored later.

## 10. C# 14 / .NET 10, idiomatic and boring
Latest LTS language and runtime, Minimal APIs, records, DI, options pattern. No cleverness that a mid-level .NET dev couldn't follow in one read. File size soft cap 400 lines, hard cap 800.
