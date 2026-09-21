# Engineering Playbook — agents, skills, and decision rules

How we run work in this repo: which agent does what, which skill loads when,
and the decisions that repeat often enough to need a diagram instead of a
debate. Grounded in incidents, not aspirations — every "avoid" below was
actually hit. Companion to AGENTS.md (hard rules) and the constitution.

## 1. Agent topology

```
Orchestrator (Hermes parent session)
├─ decompose-first: >1 substantial file or authoring+wiring+tests
│   → stig_decompose → stig_add_tasks_routed → ~15-min children
├─ coding children (parallel, disjoint slices)
├─ reviewer child (independent, every PR — never the author)
└─ mesh consults (ownership-bound, read-only outside their repo)
```

| Role | Who | When | Verify |
|---|---|---|---|
| Orchestrator | Hermes parent | always; owns gates + merges | runs full gates itself |
| Coder | delegate_task child (GLM-5.3 for CI/work; ≤3 concurrent z.ai) | implementation units | parent re-runs gates; child runs scoped checks only |
| Reviewer | separate child w/ review skills | every PR before merge | re-runs gates + read-backs; self-reports are not proof |
| Atlas consult | atlas-agent | arch-gate violations, code-graph Qs | never an owner outside Atlas repo |
| Build/k8s consult | strix-build | runner, garage, CI plane | issue-file + a2a; we never edit their plane |

## 2. The verification ladder (what proves "done")

1. `dotnet build -warnaserror` — 0 warnings
2. `dotnet test` — unit + E2E, clean env (`-u AGENTFOLIO_S3_* -u AGENTFOLIO_GITHUB_TOKEN -u NIM_*`)
3. web: `bun run test` in chat-core
4. `./scripts/arch-gate.sh` — currently noisy (atlas #473/#475); diff-mode pending; red-on-main ≠ green-to-merge
5. **Browser E2E** — real Chromium vs a staging deploy: scripted user path + zero page errors. The only gate that catches Bun-green/Chromium-broken escapes (the Illegal-invocation class, PR #58).
6. Hands-on real-data demo for anything demo-facing (constitution §3).

Skipping a rung requires naming which bug class that rung catches and why
this change can't hit it. "Tests pass" is rungs 1–3, never 5.

## 3. Decision diagrams

### 3.1 Where does a bug live?

```
Symptom in the browser
├─ error text mentions Window/brand-checked API (fetch, alert, ...)
│   → receiver-binding or env-parity bug. Reproduce in REAL Chromium
│     (CDP harness), never judge by Bun/Node behavior. Fix at the
│     constructor (bind once), not at one call site.
├─ renders but shows stale/wrong data
│   → browser-ui-debugging ladder: which bundle does the page RUN
│     (window.scripts), does the SERVED file contain the marker,
│     one server per port, is the element from THIS compile
├─ asset 200 but empty / feature invisible
│   → served-bundle vs wwwroot drift (MapStaticAssets is
│     manifest-based; the TRACKED wwwroot copy is the truth)
└─ server-side 5xx
    → AGENTS.md rule 1: named 503 + cause, never a fallback, never a trace
```

### 3.2 Which test layer catches it?

| Bug class | Layer | Catches it because |
|---|---|---|
| Logic/contract breaks | unit (vitest / xUnit) | fast, precise, runs everywhere |
| Wiring: endpoint never mapped, script tag not module, attribute name | in-proc E2E (WebApplicationFactory) | boots the real composition root |
| Env-parity (browser-only APIs, SSE in real Chromium) | browser E2E (CDP/Playwright + staging) | runs the shipped bundle in the shipping engine |
| Regressions in answer quality | eval harness (frozen set, measured) | citation-exactness et al. — never "looks fine" |
| Boundary violations | arch-gate (diff-mode) | code-graph, not grep |

Rule of thumb: **a fix ships with a test at the LOWEST layer that could have
caught the original bug** — ideally one layer up.

### 3.3 Single-checkout vs worktree vs staging?

- Edits touch ≤1 slice, no UI: code directly on the branch; gates locally.
- UI/JS changed: branch + rebuild bundle into `wwwroot/` + staging deploy
  (`dotnet <dll> --urls 127.0.0.1:<free-port>` + seeded tenant) for browser
  verification BEFORE push. Never verify UI against the live demo.
- Parallel workstreams: worktrees; export CARGO_TARGET_DIR equivalent
  (warm obj/), never two servers on one port.

### 3.4 Merge order and PR hygiene

- Green CI + mergeable → squash-merge, delete head, read back `merged: true`.
- Never merge with an open question in the PR body.
- PR description carries: root cause, fix, verification evidence (command +
  output), deploy notes. Citations or it didn't happen (constitution §2).

## 4. Skills map (load before acting)

| Trigger | Skill |
|---|---|
| Any C#/.NET authoring | dotnet10-csharp14-modern |
| Tests (xUnit, vitest, coverage) | csharp-testing-toolkit |
| Debugging anything | systematic-debugging, fail-fast-build-verify |
| Rendered page misbehaves | browser-ui-debugging (run the ladder, don't guess) |
| Serving JS through the API | dotnet-web-ui-serving (bundle-copy + module-tag traps) |
| Pre-merge review | review-critical-code-reviewer-agent, code-review, security-review (endpoints), frontend-component-review (web/) |
| Multi-file feature | plan-and-decompose / stig_decompose first |
| Long autonomy | loopit + proceed-autonomous |
| Repo state questions | repo-state-audit |

## 5. Standing agents to add (roadmap)

1. **Browser-E2E CI lane** (highest value): Playwright/CDP + cached Chromium
   on the ci-k8s host lane; scripted journey = unlock gate → upload file →
   assert streamed answer + zero window errors. Kills the env-parity class
   permanently. Skill: new `browser-e2e-agentfolio`.
2. **Independent reviewer child** wired into the PR flow: runs gates itself,
   reviews diff vs AGENTS.md conventions, posts findings before merge.
3. **Arch-gate diff-mode**: fail only on cross-slice edges NEW vs merge-base
   (atlas consult owns the fix for #473/#475 nondeterminism).

## 6. What to avoid (each one bit us)

- Judging browser behavior by Bun/Node test results — env-parity escapes
  ship broken UIs (PR #58).
- Trusting "tests pass" as "feature works" — pass ≠ exercised.
- Editing code before reproducing in the real engine (browser/server).
- Serving/staging from `dist/` and forgetting the tracked `wwwroot/` copy —
  MapStaticAssets serves the manifest, not your disk.
- Green-CI-as-final-word: CI runs rungs 1–4, never the browser.
- Arch-gate noise as background radiation — either fix the tool or gate on
  the diff; a red gate everyone ignores is a gate you don't have.
- Dispatching a monolith child because it "looks unit-shaped".
- Trusting a child's self-report; read back the artifact yourself.
- Fake success from command output — relist/read back after every
  state-changing operation (branch pushes, merges, seed writes).
- Multiple fixes per commit; one cause, one fix, one test.
