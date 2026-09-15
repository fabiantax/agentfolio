# SkillOpt csharp-review — Optimization Report

**Date:** 2026-09-15 · **Backend:** oMLX on Mac (`NVIDIA-Nemotron-3.5-Lightning-30B-A3B-oQ4-mtp` @ macbook-pro-van-fabian:8123)
**Corpus:** `.skillopt/csharp-review/tasks/train/batch_001.json` (16 items, hard-scored)
**Run:** `outputs/skillopt__csharp_review_NVIDIA-Nemotron-3.5-Lightning-30B-A3B-oQ4-mtp_20260915_172041` (8 steps, 2 epochs, wall 1412s)

## Verdict: NO IMPROVEMENT — seed skill retained

| Metric | Baseline (seed) | Best skill | Delta |
|---|---|---|---|
| Selection (val, n=8) hard | 0.875 | 0.875 | +0.000 |
| Test (n=8) hard | 1.000 | 1.000 | +0.000 |
| Full 16-item corpus hard (independent rerun) | 15/16 (94%) | **14/16 (87.5%)** | −1 |

The run's `best_skill.md` is byte-identical to the seed (`skill_len=4078` at every step; diff confirmed empty). **Deployment: not warranted** — `best-skill-DEPLOY.md` is a copy of the seed for reference only; the seed stays in place. Do not deploy to `~/.hermes/skills`.

## Per-item before/after (full 16-item corpus, hard)

Independent rerun with `run_batch` using best-skill content vs. recorded baseline (`/tmp/csr-baseline/predictions.json` → 15/16, sole miss csr-010).

| Item | Baseline | Best skill | Δ |
|---|---|---|---|
| csr-001 | 1 | 1 | 0 |
| csr-002 | 1 | 1 | 0 |
| csr-003 | 1 | 1 | 0 |
| csr-004 | 1 | 1 | 0 |
| csr-005 | 1 | 1 | 0 |
| csr-006 | 1 | 1 | 0 |
| csr-007 | 1 | 1 | 0 |
| csr-008 | 1 | **0** | −1 |
| csr-009 | 1 | 1 | 0 |
| csr-010 | 0 | 0 | 0 |
| csr-011 | 1 | 1 | 0 |
| csr-012 | 1 | 1 | 0 |
| csr-013 | 1 | 1 | 0 |
| csr-014 | 1 | 1 | 0 |
| csr-015 | 1 | 1 | 0 |
| csr-016 | 1 | 1 | 0 |
| **Total** | **15/16** | **14/16** | **−1** |

Note: the −1 on csr-008 is **not skill-caused** — the skill is unchanged; it is sampling variance at temperature 0.3 on the target model. csr-010 (Reqnroll/Gherkin) remains the only genuine, reproducible miss.

## What changed in the skill: nothing

All 8 steps ended in `skip_no_patches`: the analyst (optimizing model) produced 0 usable patches on both failure and success minibatches. Root causes:

1. **Only 1–2 failures per epoch**, all concentrated on csr-010; small edit budget (4) and patch-mode analyst produced no accepted edits.
2. The generic analyst prompts (`analyst_error.md` / `analyst_success.md`) were missing from `skillopt/prompts/` and had to be copied from the `_fab_swarm_rust` env — they are Rust-codebase-flavored, not C#-review-tailored, which likely dulled patch quality.

## Char counts

| File | Chars |
|---|---|
| Seed skill (`.skillopt/csharp-review/seed-skill.md`) | 4078 |
| Best skill (`outputs/best_skill.md` = `best-skill-DEPLOY.md`) | 4078 (identical) |

## Infrastructure issues encountered (all fixed during the run)

1. `CSharpReviewDataLoader` missing `make_base_seeds`/`shuffle_epoch_seeds` → added (parent's BaseDataLoader-inheritance fix also landed; both compatible).
2. Missing split aliasing in `build_eval_batch` (`valid_seen`/`valid_unseen` → val/test) → added `_ALIAS` map; also materialized `tasks/val/` + `tasks/test/` (8 items each, deterministic from the 16-item pool) since only `train/` existed.
3. Missing `plan_train_epoch` on the loader → added (mirrors `SplitDataLoader`).
4. Missing generic `analyst_error`/`analyst_success` prompts → copied from `_fab_swarm_rust` env.
5. oMLX/Mac: no connection refusals or sleeps observed; cold/warm latency normal throughout.

## Recommendation

The optimizer cannot improve past 15/16 with patch-mode edits because failures are too few and the analyst lacks C#-specific grounding. To actually fix csr-010, edit the seed skill directly: add an explicit rule for .NET BDD/spec frameworks (Reqnroll, Gherkin, SpecFlow — e.g. "missing `[Binding]`/step-definition attributes or `.feature` files ⇒ flag `Reqnroll`/`SpecFlow` package wiring"), then re-baseline.

## Parent addendum (2026-09-15)
csr-010 (Reqnroll/Gherkin/SpecFlow) is a testing-skill item; the optimized skill was csharp-solid-coupling — corpus/skill mismatch, not a skill gap. The BDD knowledge already lives in csharp-testing-toolkit. No seed change needed; if re-run, use a merged 3-skill corpus.
