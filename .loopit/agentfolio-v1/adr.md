# ADR Log — AgentFolio

## ADR-0001: Greenfield repo, not freshening eda_ems
Date: 2026-09-15 | Status: accepted
Survey found zero agent-framework packages in any local C# project (only eda_ems work repo, paket, Aspire, no AI deps). New repo `agentfolio` per user decision.

## ADR-0002: SQLite + sqlite-vec + FTS5 (+ graph via CTEs)
Date: 2026-09-15 | Status: accepted
EF Core 10 vector search is SQL Server 2025-only; SQLite gets vectors via the sqlite-vec extension loaded into Microsoft.Data.Sqlite (SK SQLiteVec connector approach). Graph = recursive CTEs over an edges table; no separate graph DB in v1. One file, WAL, single writer.

## ADR-0003: Model tiering — LFM2-350M browser (WebGPU) + NVIDIA NIM server
Date: 2026-09-15 | Status: accepted
LFM2-350M-ONNX q4 runs in-browser via transformers.js WebGPU (verified LiquidAI docs + onnx-community export). NIM is OpenAI-compatible → Microsoft.Extensions.AI OpenAI client. Both behind IChatClient. Fallback mandatory (recruiter laptops).

## ADR-0004: LinkedIn ingestion = Chrome extension (user decision)
Date: 2026-09-15 | Status: accepted
No public API. User chose extension over paste-import. Pattern copied from localscout clipper: WXT+TS MV3, ranked selector adapters, HTML fixture tests, offline queue in chrome.storage, batch POST to /api/ingest/saves with ingest token.

## ADR-0005: Event-lite sourcing on SQLite
Date: 2026-09-15 | Status: accepted
Append-only events table + EF projections; rebuild command tested. Full ES (Marten) rejected: Postgres-only, overkill for v1.

## ADR-0006: Web Component core + 3 thin hosts
Date: 2026-09-15 | Status: accepted
<agentfolio-chat> custom element (Lit or vanilla) with SSE + transformers.js; vue/react/angular demo hosts wrap it. One brain, three wrappers; the wrapping itself is part of the portfolio story.

## ADR-0007: Aspire → Helm → k3s on existing Hetzner VM
Date: 2026-09-15 | Status: accepted
Aspire.Hosting.Kubernetes AddKubernetesEnvironment → aspire publish generates Helm chart; CI builds+pushes image to Gitea registry, helm upgrade via kubeconfig secret. SQLite on PVC, replicas=1.

## ADR-0008: C# 14 on .NET 10 (not C# 10)
Date: 2026-09-15 | Status: accepted
User wrote "C# 10 (latest)" — C# 10 is 2021/.NET 6. Latest is C# 14 with .NET 10 (SDK 10.0.401 installed). Taking "latest" intent.
