# Launch runbook — AgentFolio × GLiFormer demo stack

Deploy target: **podman (local, strix)** and **k3s on a Hetzner VM** —
same image, same manifests. k3s is a CNCF-conformant Kubernetes
distribution: Deployments/Services/Jobs/PVCs/Ingress, kubectl and Helm
all work unchanged — this counts as Kubernetes experience; write
"Kubernetes (k3s, Helm)" on the CV.

Stack:

```
client ─► agentfolio api+web (Aspire chart, k8s-artifacts/)
              │
              ▼ HTTP (veld /ner + /extract contract)
          gliformer :8080  — knowledgator/gliformer-base-v1
              │            (NER + joint relations, CPU, ~1.2 GiB RSS)
              ▼ optional
          veld ingest (ml-knowledgegraph) → embedded GraphFusion KG
```

Images: built on strix with podman, pushed to the self-hosted Gitea
registry `100.112.37.119:3200` (plain HTTP over the tailnet — the VM's
k3s gets a containerd mirror via `registries.yaml` in cloud-init).

---

## A. Local podman (strix) — build + run + smoke

```bash
cd ~/Developer/personal/agentfolio/deploy/demo-stack

podman build -f Containerfile.gliformer \
  -t 100.112.37.119:3200/fabiantax/gliformer:v0.1.3 .

podman run -d --name gliformer-demo -p 18080:8080 --memory 3g \
  100.112.37.119:3200/fabiantax/gliformer:v0.1.3
# NOTE --memory 3g: fp32 model load PEAKS >2 GiB (OOMKilled at 2g, measured
# 2026-09-15); steady-state RSS is ~1.7 GiB. Same numbers are in k8s limits.

# model loads lazily — /health shows model_loaded:false until first call
curl -s localhost:18080/health | jq
curl -s localhost:18080/ner -H 'content-type: application/json' \
  -d '{"text":"Alice works at Acme Corp in London.","labels":["person","organization"],"threshold":0.5}' | jq
```

Veld swap-in (ml-knowledgegraph side): point the existing Rust
`VeldExtractorsAdapter`/`veld ingest --extractors http://localhost:18080`
at this service — same wire contract as the gliner2 bundle.

## B. Push to the Gitea registry

```bash
podman login 100.112.37.119:3200 -u fabiantax \
  -p "$(token from ~/.config/gitea)"
podman push 100.112.37.119:3200/fabiantax/gliformer:v0.1.3
```

## C. One-time — bake the k3s base snapshot (~10 min, then €0 idle)

```bash
cd ~/Developer/personal/agentfolio/deploy/demo-stack
sed 's/TS_AUTHKEY_PLACEHOLDER/tskey-XXXX-.../' cloud-init-k3s.yaml > /tmp/ci-rendered.yaml

hcloud server create --name k3s-demo-base --type cx33 --location fsn1 \
  --image ubuntu-24.04 --user-data-from-file /tmp/ci-rendered.yaml \
  --ssh-key "$(hcloud ssh-key list -o noheader -o columns=name | head -1)"

# watch cloud-init finish (~6-8 min: tailscale + k3s + node Ready)
ssh root@$(hcloud server ip k3s-demo-base) 'cloud-init status; kubectl get nodes'

# freeze it — next launches boot from this snapshot in ~15 s
hcloud server create-image --type snapshot --description k3s-demo-base-v1 \
  --label demo=k3s-base k3s-demo-base
hcloud server delete k3s-demo-base     # snapshot survives; ~€0.05/mo storage
```

## D. Per-demo launch (~3 min total)

```bash
SNAP=$(hcloud image list --type snapshot -o noheader -o columns=id --selector demo=k3s-base | head -1)
hcloud server create --name agentfolio-demo --type cx33 --location fsn1 \
  --image $SNAP --start-after-create

sleep 30 && ssh root@$(hcloud server ip agentfolio-demo) 'kubectl get nodes' # Ready

# kubeconfig onto strix (separate file; strix's own k3s stays default)
scp root@$(hcloud server ip agentfolio-demo):/etc/rancher/k3s/k3s.yaml kubeconfig-hetzner
export KUBECONFIG=$PWD/kubeconfig-hetzner

kubectl apply -f k8s/                      # ns + gliformer + smoke Job
kubectl -n agentfolio wait --for=condition=ready pod -l app=gliformer --timeout=300s
kubectl -n agentfolio logs job/gliformer-smoke   # → "SMOKE PASSED"

# AgentFolio app itself (Aspire-generated chart; af-11 notes):
helm upgrade --install agentfolio ../../k8s-artifacts -n agentfolio \
  --set parameters.api.api_image=100.112.37.119:3200/fabiantax/agentfolio-api:<tag>
# + PVC for SQLite (docs/deploy/k8s-notes.md) + Traefik Ingress for a URL
```

Client demo URL options, pick per audience:
1. Tailscale MagicDNS `http://k3s-demo:8080` (share tailnet invite) — zero public exposure
2. Traefik Ingress + cert-manager on a real domain — full k8s ingress/TLS story

## E. Teardown (demo over → €0 compute)

```bash
hcloud server delete agentfolio-demo
```

Snapshot + Gitea registry image persist. Next demo = §D only.

## Costs (Apr-2026)

| Item | Cost |
|---|---|
| CX33 8 GB (recommended, client-facing) | €6.49/mo ≈ €0.011/h — 2 h demo ≈ €0.02 |
| CX23 4 GB (works, tight: gliformer 2Gi limit + api + k3s) | €3.99/mo |
| Snapshot storage | ~€0.011/GB/mo (~€0.05) |

## Honest sizing + CV claims

- gliformer-base on CPU: NER 94 ms, joint relations 64 ms (strix,
  2026-09-15, single text) — quote as "sub-150 ms CPU inference".
- **A/B context** (gliformer-service/VERDICT.md, same-day measured):
  GLiFormer-LARGE loses single-request lanes to GLiNER 2.5 (470 vs 88 ms)
  but WINS batch (39 ms/text @ bs16, 5 heads, one model) — this demo ships
  BASE, which is why its single-text CPU latency is ~115 ms. "I measured
  both and picked per workload" is the client story.
- Long input (v0.1.2): >4000 chars auto-CHUNKS — sentence-aligned 3200-char
  chunks, 2-sentence overlap at seams, offsets remapped to DOCUMENT
  coordinates, results deduped. Measured: 10,267-char doc → 4 chunks, 39
  entities, ALL injected entities found, every offset exact
  (test_live_chunked.py; chunker invariants: test_chunker.py).
  413 only beyond a 50k hard ceiling.
- Progressive context (v0.1.3): entities found in chunks 1..N-1 are
  re-matched in every later chunk + a final full-doc consolidation sweep —
  a later chunk cannot "forget" what earlier chunks established. Model-found
  always wins; propagated mentions carry source="propagated" and the
  response reports the count. Verified by exact accounting (thr 0.7 stress:
  model dropped 3 weak-context mentions, propagation rebuilt exactly 3).
  `propagate:false` on the request A/Bs the delta live. Relations stay
  model-only; gliformer 0.1.2 has no few-shot exemplar API (checked from
  source) — this is deterministic context, honestly labeled.
- GLiFormer is Knowledgator's framework (Apache-2.0, 2026-09-11) — a
  successor in the GLiNER family line, NOT of fastino's gliner2.5. Say it
  correctly in front of clients.
- k8s story for the CV: multi-tier Deployment, self-hosted OCI registry
  + containerd mirrors, Jobs, PVCs, Ingress/TLS via Traefik + cert-manager,
  resource requests/limits, snapshot-based infra fast-launch.
