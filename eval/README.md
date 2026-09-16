# AgentFolio eval (issue #44)

Measured, reproducible quality numbers for the chat pipeline — the product's
"VERIFY THIS DEMO" bar applied to its own ML claims.

## Harness

`tools/AgentFolio.Eval` boots the REAL pipeline (WebApplicationFactory on the
actual `Program`), seeds the eval set's sources through the REAL upload +
sandbox-extraction path, then asks every question through the REAL `/chat` SSE
endpoint against a configured OpenAI-compatible model. No fakes; the only
boundary objects are temp-dir SQLite stores (same as the E2E suite).

## Frozen metric definitions (do not change per run)

- `citation_exactness` — fraction of questions whose answer carries >=1
  citation URL equal to the question's gold source URL (trailing-slash and
  case insensitive). Empty-retrieval and citation-less answers count AGAINST
  this rate, never as a pass.
- `answer_has_citation_rate` — fraction of answers carrying >=1 citation of
  any URL (catches the bare-link problem separately from exactness).
- `ner_f1` — entity-over-answer matching as a lightweight NER proxy: the
  vocabulary is the union of all questions' gold entities plus the set's
  distractor entities; a hit is a case-insensitive ordinal substring match in
  the answer text. Per question: precision = gold-vocab hits / all vocab hits
  (parroting other questions' entities or distractors is punished), recall =
  gold entities present / gold entities. `ner_f1` is the macro average of
  per-question F1. This measures answer grounding in the source vocabulary —
  a real NER extractor's P/R/F1 will be added when the extractor produces
  entities (tracked in #44's Deferred section).
- `latency_p50_ms` / `latency_p95_ms` — nearest-rank percentiles of the full
  request wall time (POST /chat through `[DONE]`).

## Run

```bash
dotnet run --project tools/AgentFolio.Eval -- \
  --set eval/sets/v1.json --out eval/results/v1.md \
  --model nemotron-4b --api-base http://127.0.0.1:8007/v1 --api-key eval-local
```

Model config falls back to `NIM_MODEL` / `NIM_BASE_URL` / `NIM_API_KEY`.
Any OpenAI-compatible endpoint works; results files record model + endpoint.

## Sets discipline

`eval/sets/*.json` are frozen once a results file references them: no edits,
only a new `vN.json`. Questions cite `sourceId` so citation-exactness has a
single gold URL per question.
