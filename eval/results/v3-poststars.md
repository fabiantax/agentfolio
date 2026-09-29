# AgentFolio eval results — set v1

- run: 2026-09-20T10:04:10Z
- model: `nemotron-4b` @ `http://127.0.0.1:8007/v1`
- pipeline: real Program (upload -> sandbox extract -> /chat SSE); no fakes

## Metrics (definitions frozen in eval/README.md)

- `citation_exactness` — **80.0%** (16/20): >=1 citation URL equals the gold source URL
- `answer_has_citation_rate` — **80.0%** (16/20)
- `ner_f1` (macro over questions, substring-vocab match) — **0.728** (P 0.800 / R 0.690)
- `latency_p50_ms` — **4138**; `latency_p95_ms` — **11154**

## Per-question

| id | citation-exact | entity P/R/F1 | latency ms |
|---|---|---|---|
| q01 | yes | 1.00/0.50/0.67 | 11154 |
| q02 | yes | 0.00/0.00/0.00 | 3606 |
| q03 | no | 1.00/1.00/1.00 | 5586 |
| q04 | yes | 1.00/0.50/0.67 | 2788 |
| q05 | yes | 1.00/1.00/1.00 | 3899 |
| q06 | no | 1.00/0.50/0.67 | 5763 |
| q07 | yes | 0.00/0.00/0.00 | 4636 |
| q08 | yes | 0.00/0.00/0.00 | 3618 |
| q09 | yes | 0.00/0.00/0.00 | 4775 |
| q10 | no | 1.00/0.50/0.67 | 3538 |
| q11 | yes | 1.00/0.80/0.89 | 4223 |
| q12 | yes | 1.00/1.00/1.00 | 4425 |
| q13 | yes | 1.00/1.00/1.00 | 4138 |
| q14 | no | 1.00/1.00/1.00 | 4868 |
| q15 | yes | 1.00/1.00/1.00 | 3001 |
| q16 | yes | 1.00/1.00/1.00 | 4098 |
| q17 | yes | 1.00/1.00/1.00 | 3560 |
| q18 | yes | 1.00/1.00/1.00 | 3445 |
| q19 | yes | 1.00/1.00/1.00 | 4232 |
| q20 | yes | 1.00/1.00/1.00 | 11772 |

## Notes

- Empty-retrieval / no-citation answers count AGAINST the rates above (never as pass).
- Known state at first run: /chat emits `citations: []` (retrieval wiring pending, ChatEndpoints.cs) —
  citation_exactness 0% is the honest baseline this harness exists to publish.
