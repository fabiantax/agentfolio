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

## Ruled (for the record)

- 2026-09-28: GraphFusion stays the retrieval store for #66 stage C (#66 comment 77005).
- 2026-09-28: /stars page size 100 (clamped 1..500) — merged #68.
