# AgentFolio eval results — set v1

- run: 2026-09-16T20:59:11Z
- model: `nemotron-4b` @ `http://127.0.0.1:8007/v1`
- pipeline: real Program (upload -> sandbox extract -> /chat SSE); no fakes

## Metrics (definitions frozen in eval/README.md)

- `citation_exactness` — **85.0%** (17/20): >=1 citation URL equals the gold source URL
- `answer_has_citation_rate` — **90.0%** (18/20)
- `ner_f1` (macro over questions, substring-vocab match) — **0.711** (P 0.800 / R 0.665)
- `latency_p50_ms` — **4344**; `latency_p95_ms` — **9150**

## Per-question

| id | citation-exact | entity P/R/F1 | latency ms |
|---|---|---|---|
| q01 | yes | 1.00/0.50/0.67 | 9150 |
| q02 | yes | 0.00/0.00/0.00 | 5709 |
| q03 | no | 0.00/0.00/0.00 | 4511 |
| q04 | yes | 1.00/0.50/0.67 | 4827 |
| q05 | yes | 1.00/1.00/1.00 | 4943 |
| q06 | yes | 1.00/0.50/0.67 | 4985 |
| q07 | yes | 1.00/0.50/0.67 | 4254 |
| q08 | yes | 0.00/0.00/0.00 | 3237 |
| q09 | yes | 1.00/1.00/1.00 | 3942 |
| q10 | yes | 1.00/0.50/0.67 | 3509 |
| q11 | yes | 1.00/0.80/0.89 | 4456 |
| q12 | yes | 1.00/1.00/1.00 | 4852 |
| q13 | yes | 1.00/1.00/1.00 | 3175 |
| q14 | yes | 1.00/1.00/1.00 | 4120 |
| q15 | yes | 1.00/1.00/1.00 | 5810 |
| q16 | no | 1.00/1.00/1.00 | 4344 |
| q17 | yes | 1.00/1.00/1.00 | 3434 |
| q18 | yes | 1.00/1.00/1.00 | 3231 |
| q19 | no | 1.00/1.00/1.00 | 3984 |
| q20 | yes | 0.00/0.00/0.00 | 12453 |

## Notes

- Empty-retrieval / no-citation answers count AGAINST the rates above (never as pass).
- Known state at first run: /chat emits `citations: []` (retrieval wiring pending, ChatEndpoints.cs) —
  citation_exactness 0% is the honest baseline this harness exists to publish.
