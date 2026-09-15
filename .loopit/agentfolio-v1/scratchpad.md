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

## Wave-1 close-out (2026-09-15 ~17:00)
- All 3 children done; af-14 died at max_iterations AFTER completing work (known pattern) — parent salvaged uncommitted tree, removed planted Illegal.cs, committed.
- Children shared main checkout (no worktree isolation) — commits interleaved on ONE branch feat/af-14-atlas-gate; it became the integration branch. LESSON: dispatch disjoint-dir children to shared checkout is fine, but tell children to commit+push in their OWN branch from a worktree, or accept single integration branch deliberately.
- .atlas/ cache churn got committed by child (6408 lines) — gitignored now, lesson for CI: gitignore BEFORE children run atlas.
- arch-gate caught 2 REAL violations (template Class1 stubs: contracts-purity, agents-no-platform) — dogfood proof. Removed.
- Parent gates: build 0 warn, 8/8 tests, arch-gate 4/4. PR #1 open (55 files +3287/-341).
- Mac lane: oMLX revived (down since 09-11), nemotron-3.5-lightning verified 6.6s cold, provider omlx-nemotron registered. Wave-2 easy tasks -> Mac lane via hermes chat -q subprocess pattern.
- stig: af-01, af-09, af-14 claim+completed by hermes-main.

## BUILD PHASE CONVERGED (2026-09-15 19:35)
13 of 14 stig tasks DONE (af-01..05, 06..12, 14-gate; af-10 last via PR #25).
Remaining: af-03 (CI verify — runner-label infra), af-13 (demo acceptance — USER SECRETS).
Final tree: main c09bdbb, 49/49 dotnet tests, chat-core 19/19, arch-gate 4/4, 10 PRs merged (#1,17-25).

### Succession
- OPEN CANDIDATE: demo-deploy loop (af-13 + k3s apply + extension live-test) — BLOCKED on GITHUB_TOKEN, NIM_API_KEY, AGENTFOLIO_PASSWORD_HASH (user env, never chat). DECLINED to open until secrets exist.
- FILED: issue #16 hardening backlog (atlas P0x2, fab-swarm P0-P3) — cross-repo follow-up.
- FILED: oMLX DFlash2 upgrade fact #304; pkill/pgrep self-match anti-pattern → loopit skill (measured 2x).

### Improvement cycle artifacts landed this loop
1. ms-agent-framework-dotnet skill: 3 execution-verified MAF 1.21 corrections (AsAIAgent, M.E.AI>=10.10.0, session-required streaming).
2. loopit skill: pgrep/pkill -f self-match anti-pattern.
3. atlas-clean-arch-gate skill + live 4-rule gate in repo (caught 2 real violations).
4. Hardening backlog issue #16 (8 evidence items).
5. Nemotron-lane defect pattern: BOM'd csproj + object-initializer-on-positional-records (caught af-04+af-05) — future nemotron prompts get a pre-flight instruction.

### Lane ledger (measured today)
- z.ai glm-5.3-flash children: 8 dispatched, 8 completed, 0 hit max_iterations-uncommitted after worktree isolation adopted mid-session (3 before).
- Mac nemotron children: 4 dispatched (af-04/05/06 + 1 earlier), all needed parent repair (BOM, ctor shapes); af-06 needed 3 logic-bug fixes (deadlock, lossy events, rebuild-append loop).
- DFlash2: NOT loadable (oMLX too old); Qwen3.8-27B-4bit: verified working 84 tok/s clean / 17-27 shared.
- SkillOpt: full dogfood run (env pkg + corpus + oMLX backend), honest no-improvement verdict, corpus/skill mismatch identified.

## PIVOT: v1.1 MULTI-TENANT SAAS (2026-09-16 00:05-01:35)
User decision: full multi-tenant SaaS (not visitor-uploads-only). Spec 002 written + pushed
(specs/002-multi-tenant-saas/spec.md); milestone 200 created; issues #27-#31 (S1 tenancy ->
S2 OAuth || S3 uploads -> S4 sandbox -> S5 share+migration); issue #26 folded into spec 002.
Kills af-13's GITHUB_TOKEN blocker (per-tenant OAuth instead of admin PAT).

### Incident: fab-swarm swarm.db fleet wedge (fab-swarm#140)
stig DAG registration impossible: MCP calls 420s/30s timeouts; root = duplicate mcp-proxy spawn
+ WAL-recovery lock storm (ro opens can't recover dirty shm; 8 respawning holders). Workaround:
Gitea issues = board, delegate_task = dispatcher (proven). NOT fixed live (other sessions' servers).
Diagnostic lessons: (1) fuser <db> before killing DB holders; (2) ro sqlite mode CANNOT recover WAL —
wedge diagnosis needs an rw open attempt; (3) kills rejected for nohup wrappers never ran — verify
PID death, don't trust command exit 0.

### v1.1 wave state
S1 (deleg_c44b9c0c) dispatched 01:35, glm-5.3-flash, worktree /tmp/s1-tenancy.
S2/S3 parallel after S1 merges; S4 after S3; S5 last.
