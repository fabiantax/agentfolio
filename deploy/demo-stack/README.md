# demo-stack — client-demoable ML extraction (GLiFormer × AgentFolio)

A self-contained NER + relation-extraction service you can put in front of a
client in minutes: open a URL, paste a document, watch entities get
highlighted and relations extracted — locally on podman or on a k8s cluster,
from the same image.

Built as the ML-credibility demo for [AgentFolio](../../README.md): a
freelancer's answer to "do you actually have hands-on ML/NLP experience?"
is a running system with measured numbers, not a CV line.

## The 30-second pitch

- **Model**: `knowledgator/gliformer-base-v1` (released 2026-09-11,
  Apache-2.0, 264M DeBERTa encoder) — zero-shot NER + joint relation
  extraction + classification, labels supplied at inference time.
- **Engineering around it** (the actual differentiator):
  sentence-aligned chunking with exact document-global offsets, progressive
  context (entities found in chunks 1..N-1 are carried into chunk N),
  memory-capped containers, a self-hosted OCI registry path, k8s manifests,
  and an exact-accounting test suite.

## Quick start (local, strix)

```bash
cd deploy/demo-stack
podman build -f Containerfile.gliformer \
  -t 100.112.37.119:3200/fabiantax/gliformer:v0.1.4 .
podman run -d --name gliformer-demo --network=host --memory 3g \
  100.112.37.119:3200/fabiantax/gliformer:v0.1.4 \
  uvicorn gliformer_main:app --host 127.0.0.1 --port 18080
```

Open **http://127.0.0.1:18080/** — the built-in demo UI. Paste a document,
edit the entity/relation schema live (labels are free text; the model
re-conditions instantly), drag the threshold, toggle progressive context.

Or curl the API:

```bash
curl -s localhost:18080/ner -H 'content-type: application/json' \
  -d '{"text":"Alice works at Acme Corp in London.","labels":["person","organization"],"threshold":0.5}'
```

## Measured numbers (all from this machine, 2026-09-15/16)

| Claim | Number | Source |
|---|---|---|
| NER latency (single text, CPU) | 94–131 ms | live smoke |
| Joint extract (entities+relations, CPU) | 82–115 ms warm | live smoke |
| Long doc (10.3k chars) | 4 chunks, 39 ents, every offset exact | test_live_chunked.py |
| Progressive context (thr 0.7 stress) | model dropped 3 mentions, propagation rebuilt exactly 3 | exact-accounting A/B |
| Demo UI round-trip (multi-chunk doc) | 2.7 s warm, 26 ents (17 model + 9 propagated) | browser-verified |
| Memory | 1.62 GiB steady, >2 GiB load peak → 3g cap required | OOMKill forensics |
| GLiFormer-large vs GLiNER 2.5 (prod lane) | 470 vs 88 ms single-text — large is a BATCH model | gliformer-service/VERDICT.md |

## Layout

| File | What |
|---|---|
| `gliformer_main.py` | The service: veld wire contract (/ner, /extract), chunker, EntityVocab progressive context, input bounds |
| `demo.html` | Client-facing demo UI served at `/` (no build step, same-origin fetch) |
| `Containerfile.gliformer` | CPU-only image, model baked in (reproducibility over size) |
| `k8s/gliformer.yaml` | Deployment + Service, memory-capped (3Gi limit — see table) |
| `k8s/smoke-job.yaml` | In-cluster proof Job: runs a real /extract, asserts entities + relation |
| `cloud-init-k3s.yaml` | Hetzner VM bootstrap: k3s + Gitea registry mirror + Tailscale |
| `test_chunker.py` | Offline invariants (verbatim-slice offsets, coverage, vocab semantics) — stdlib only |
| `test_live_chunked.py` | Live end-to-end incl. propagate on/off exact-accounting A/B |
| `LAUNCH.md` | Runbook: local podman → registry → Hetzner snapshot → per-demo launch (~3 min) |
| `QUALITY-LEVERS.md` | Quality research: official docs, measured lessons, lever ladder, config pre-fill design |

## Design decisions worth knowing

1. **Contract-compatible with veld** — speaks the same /ner + /extract
   contract as the gliner2 bundle in ml-knowledgegraph, so the Rust
   `VeldExtractorsAdapter` swaps backends by changing a URL.
2. **Chunking beats rejection** — GLiFormer silently returns zero entities
   past ~4-5k effective tokens (measured); this service chunks
   sentence-aligned with a 2-sentence overlap and remaps offsets exactly
   (the verbatim-slice invariant is unit-tested), 413 only beyond 50k.
3. **Progressive context is deterministic, and labeled as such** — no
   few-shot exemplar API exists in gliformer 0.1.2 (source-verified), so
   cross-chunk context is exact string propagation (`source:
   "propagated"`), never presented as model output. Relations stay
   model-only.
4. **Every quality claim is measured** — thresholds flip between golds
   (ml-knowledgegraph ADR-007), so nothing ships without the anchored eval.
   See QUALITY-LEVERS.md before proposing tuning.

## Deployment

- **Local**: quick start above.
- **Hetzner k8s**: LAUNCH.md §C/§D — one-time snapshot bake (~10 min), then
  per-demo launches in ~3 min (15 s VM boot from snapshot + `kubectl
  apply`). Image: `100.112.37.119:3200/fabiantax/gliformer:v0.1.4` (Gitea
  container registry over the tailnet).

## Version history

| Tag | Change |
|---|---|
| v0.1.0 | initial: contract service, model baked, joint relations |
| v0.1.1 | 413 input bound (silent-zero trap) |
| v0.1.2 | sentence-aligned chunking + exact offsets + dedup |
| v0.1.3 | progressive context (EntityVocab) + consolidation sweep + propagate flag |
| v0.1.4 | built-in demo UI at `/` |
| v0.1.5 | `/local` — LFM2.5-350M chat running 100% client-side (transformers.js, WebGPU/wasm) |
| v0.1.6 | domain bootstrap: `/schema/bootstrap` fetches the client's site → fired-label schema with their vocabulary + gazetteer seeds (seed_entities on /ner+/extract, guaranteed-recall first shots) |

## Open (honest)

- AgentFolio .NET wiring: chat/uploads → extraction (product path)
- veld Rust ingest pointed at this service
- anchored A/B harness (base×large × label styles × thresholds)
- config pre-fill: gazetteer seeding + schema defaults + aliases
- Hetzner snapshot bake (blocked on Tailscale authkey)
