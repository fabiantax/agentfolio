# Loopit: agentfolio-v1

## Goal
Build AgentFolio per specs/001-portfolio-assistant: password-gated C#/.NET 10 portfolio chat (Microsoft Agent Framework, Aspire, SQLite vec/FTS/graph), GitHub-stars + LinkedIn-saves ingestion (Chrome extension, localscout pattern), spec-driven (spec-kit), Team-Topologies-mapped, fab-swarm DAG-orchestrated, deployed to Hetzner k3s.

## Status block
- Autonomy: L2 (PR behind CI gate) — earn L3 later
- Cadence: session-driven loopit; fab-swarm store is the work queue
- Token budget: parent + ≤3 children, subagent model for mechanical units
- Escalation: user decisions = scope/model/API keys/deploy targets
- Iteration: 1 (bootstrap)
- CI: pending (Gitea workflow to be added)

## Items (executed THIS loop; queue lives in fab-swarm)
- [ ] I1 repo bootstrap: solution, projects, Directory.Build.props, gitignore, Gitea remote+push — gate: `dotnet build -warnaserror` green
- [ ] I2 spec-kit artifacts committed (constitution, spec 001, team-topologies) — gate: files on main
- [ ] I3 fab-swarm store initialized + DAG queued (stig_add_tasks_routed) — gate: stig_dag_waves returns waves
- [ ] I4 CI workflow on Gitea (build+test on push) — gate: first green run on main
- [ ] I5 platform skeleton: Platform project + auth slice skeleton + SQLite EF store skeleton — gate: build green + store opens DB WAL
- [ ] I6 extension scaffold (WXT) + LinkedIn saved-posts adapter — gate: vitest fixture tests green
- [ ] I7 agents skeleton: IChatClient tiering (NIM client + browser-tier contract), agent factory — gate: build green + unit test tier selection
- [ ] I8 chat API slice: SSE streaming endpoint + citations contract — gate: integration test streams tokens
- [ ] I9 GitHub stars sync worker — gate: syncs real stars with PAT (user-provided), stores entities+events
- [ ] I10 web-component chat + 3 hosts (vue/react/angular) — gate: builds with bun, component renders in each host
- [ ] I11 deploy: Aspire AppHost + k8s publish + Helm on Hetzner k3s — gate: hands-on Tailscale demo, password gate live
- [ ] I12 demo acceptance: real-data chat with citations, browser-tier + server-tier — gate: USER VERDICT

## Findings (newest first, max 20)
- 2026-09-15: strix has no prior C# code except ems-blastradius (work, paket, no AI deps) → greenfield.
- 2026-09-15: .NET SDK 10.0.401 installed at ~/.dotnet (was absent).
- 2026-09-25: spec-kit 1.0.8.dev0 specify CLI works non-interactively with --force.
- 2026-09-15: localscout extension = WXT+Svelte MV3, ranked selectors, fixture tests, offline queue → exact blueprint for saves-harvester.
- 2026-09-15: Agent Framework GA v1.21 (2026-09-11), net8/9/10, IChatClient-based.
- 1.16→1.21 minor churn: pin Agent Framework 1.21.x explicitly in Directory.Packages.props.

## Blockers
- GitHub PAT for stars sync (user to provide via env, never chat) + NVIDIA NIM API key (env only). Need user env setup before I9 runtime test; build-time unaffected.

## Recent runs
- 2026-09-15 iter 1: bootstrap (this)

## Iteration 1 dispatch log (2026-09-15 ~16:45)
- Wave 1 children DISPATCHED (3 parallel, deleg_4fb37e33):
  - sa-0: af-01-sqlite-store (branch feat/af-01-sqlite-store) — Directory.Packages.props + Contracts ports + Platform SqliteStoreBootstrap + tests
  - sa-1: af-09-extension (branch feat/af-09-extension) — WXT TS extension, saves harvester, vitest fixtures
  - sa-2: af-14-atlas-gate (branch feat/af-14-atlas-gate) — generalize slice rules, R4 EF-encapsulation, CI wiring, planted-violation proof
- Skills created: ms-agent-framework-dotnet (grounded v1.21.0), atlas-clean-arch-gate (measured: scan 12s/406 nodes, store at ~/.atlas/graphfusion/<repo>)
- atlas dogfood VERIFIED: scan→graphfusion-query→3-rule gate green on scaffold (scripts/arch-gate.sh, exit 0)
- Repo live: http://localhost:3200/fabiantax/agentfolio (main b9677da), CI workflow pushed
- Remaining queue: af-02 auth, af-03 CI verify, af-04 stars sync, af-05 saves ingest, af-06 events rebuild, af-07 agents skeleton, af-08 chat SSE, af-10 web component, af-11 aspire k8s, af-12 skills slice, af-13 demo acceptance
