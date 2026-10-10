# Owner decisions — pending (agentfolio)

Living checklist of decisions parked with the owner. Update the SAME TURN anything parks or
unparks. Bulk format: pick + why per item; owner approves or overrides in one reply.

## Parked 2026-09-28

| # | Decision | Recommendation | Gates | Effort | How to answer |
|---|----------|----------------|-------|--------|---------------|
| 1 | SurrealDB as retrieval/storage store (vs GraphFusion) | **Keep GraphFusion for stage C** (live on :50052, hybrid BM25+dense+RRF declared, B4/B5 write target, demo story). Pilot SurrealDB *embedded* SDK for the post-milestone storage refactor — measured against GF before any swap. | Post-#66 milestone | Pilot ~1 session | "1 approved" or "override: SurrealDB now" |
| 2 | LanceDB as vector/FTS store | **Declined**: no official C# SDK (16-star community P/Invoke wrapper), no graph traversal (multi-hop becomes agentfolio-side code). Revisit only if the corpus outgrows GF's HNSW (>1M vectors). | n/a (declined) | — | Override anytime |
| 3 | Share-visitor force-sync scope: any unlocked visitor can POST /stars/sync?force=1 | **Scope to owner** (rate-limit safety; visitors poll status only). One-line change. | #66 stage C deploy | ~15 min | "3 approved" or "override: keep open" |
| 4 | Stage-C embedding model: embed :8082 (jina-v5-nano, already running) vs GLiFormer embed_text (1024-dim, same service as NER) | **embed :8082** — do not mix vector spaces; GLiFormer embeddings are a fallback if :8082 goes away. | #66 stage C | 0 (decision only) | "4 approved" or "override: GLiFormer" |
| 5 | Tenant GF graph has NO HNSW index; dense-arm union (would fix sql-flow-class coverage misses without waiting for enrichment) requires building `agentfolio_stars_v2`-style index on the SHARED gf-a2a (:50052) | **Skip dense union for M270**: keyword recall + ColBERT over enriched docs is the measured winner (9/10-class); #108 backfill closes the gap structurally. Revisit only if backfill leaves q09 failing AND the owner accepts a shared-infra index build (restart window on gf-a2a, other tenants' graphs on the same service). | #108 / #81 eval AC | index ~1 min build + gf-a2a restart (shared service) | "5 approved" or "override: build index now" |
| 6 | Eval bar for #81-adjacent work: live answer-level stuck at 8/10 (3 sweeps post-#111) while retrieval-level is 9/10. The 2 residuals are (a) q09 sql-flow — eval vocabulary ("ETL", "move and transform") absent from the repo's own README (0/0/1 occurrences), so README-grounded enrichment cannot honestly fix it; (b) q10 — answer-layer closest-match picks playwright over browser-harness-js when both are in-prompt (both ARE browser-check harnesses). | **Pick one**: (i) accept 8/10 live + 9/10 retrieval as the M270 bar with residuals documented (my recommendation — the pipeline is measured sound; the misses are eval-wording artifacts), (ii) reword q09/q10 to the projects' own vocabulary and re-bar at 9/10 live, (iii) approve answer-layer org-exact preference (browser-use org boost — risks overfitting the eval). | #110 / #81 | (i) 0 (ii) 10 min (iii) ~1 h + overfit risk | "6i" / "6ii" / "6iii" |

## Ruled (for the record)

- 2026-09-28: GraphFusion stays the retrieval store for #66 stage C (#66 comment 77005).
- 2026-09-28: /stars page size 100 (clamped 1..500) — merged #68.
