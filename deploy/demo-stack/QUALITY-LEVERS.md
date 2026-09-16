# Quality levers — GLiFormer extraction (researched 2026-09-16)

Primary sources: Knowledgator/GLiFormer README (Training/Evaluation/Usage
sections), HF model cards (base/large v1), docs.knowledgator.com, gliformer
0.1.2 source (installed), plus this machine's own measured history
(ml-knowledgegraph evaluation/anchored + extraction-strategy.md).

## What the official docs say

1. **Labels at inference time are free text** — "describe the entities,
   classes, and relations you need with readable labels." Labels are
   embedded and matched against spans in latent space (GLiNER mechanism).
   There is NO document-context/preamble parameter: labels ARE the context
   channel. Descriptive label phrasing is the official zero-shot lever.
2. **Fine-tuning**: `model.train_model(train_dataset=...)` with
   `freeze_components=["text_encoder"]` or `train_head_only=True` (heads
   only — cheap domain adaptation). Held-out `eval_dataset` supported.
   Records carry `ner: [[span, label], ...]` + `all_labels`.
3. **Structured extraction**: nested Pydantic schemas via
   `model.structure(text, {...}, validate_output=True)` — company →
   departments → employees in one call (needs multi_level checkpoint).
   Schema-validated output beats free NER+RE when the shape is known.
4. **Joint relation head > open head** for v1 checkpoints — typed
   entity+relation schemas together (what our /extract uses).
5. **Official evaluator**: `gliformer_eval/eval_ner.py` — strict
   entity-level P/R/F1 on char spans; CrossNER format (labels.json +
   test.json per dataset). Our anchored gold can be emitted into it.
6. **No few-shot exemplar API** (verified from source — `entities` dict =
   named label groups, not examples).

## Measured lessons on this machine (authoritative for this stack)

- Thresholds FLIP between golds: 0.3 > 0.5 on noisy gold (+31%), REVERSED
  on human-anchored gold (ADR-007 in ml-knowledgegraph). Never ship a
  threshold without anchored measurement.
- Descriptions = zero-shot lever; weights = gradient lever; GEPA optimizes
  description text but optimizes NOISE against a noisy gold (proved twice).
  Eval first; everything else downstream.
- Relation quality lives in specialized models: GLiNER-Relex beat GPT-5.5-
  mini on document-level RE (DocRED 31.3 vs 18.6) — mix models per lane.
- GLiFormer-LARGE loses single-request latency (470 vs gliner2.5's 88 ms,
  gliformer-service/VERDICT.md) but wins batch (39 ms/text @ bs16, 5
  heads). Base-vs-large QUALITY delta: UNMEASURED — needs the harness.
- GLiFormer silently returns ZERO entities past ~4-5k effective tokens
  (8k-char probe). Service chunks (v0.1.2+) + propagates (v0.1.3).

## Lever ladder (cheapest first)

| # | Lever | Cost | Status for us |
|---|---|---|---|
| 1 | Composite labels `"label — description"` | free | GAP: our service drops the veld contract's descriptions (passes label keys only) — A/B it |
| 2 | Threshold sweep on anchored gold | minutes | unmeasured for gliformer |
| 3 | Structured-extraction schema | cheap | unused in demo |
| 4 | Config pre-fill (known context) | ~30 min | design below, not built |
| 5 | Head-only fine-tune on 50-200 docs | hours | needs annotated set |
| 6 | Model-mixing (Relex for relations) | config | already running on box |

## Config pre-fill design (known-domain scenario)

- **Gazetteer seeding**: config lists known entities (label → strings);
   seed EntityVocab BEFORE chunk 1 → every exact mention guaranteed from
   the first chunk (score 1.0, source "config"); model still finds new
   ones. ~20 lines on top of v0.1.3 machinery.
- **Domain schema defaults**: entity labels + relation types as
   server-side config (request can still override).
- **Alias canonicalization**: config alias map ("Acme" → "Acme Corp") —
   raises downstream graph quality (veld feedback-loop goal).
- Caveats: config entities are unverified string matches — enforce
  min-length/phrase rules ("Will"-type false positives); config can seed
  entity STRINGS and relation TYPES, not relation INSTANCES.

## Unseen-domain playbook

Composite labels → threshold sweep on a small anchored sample → schema
constraint if shape known → head-only fine-tune → route relations to
GLiNER-Relex. All gated on the anchored eval.

## Next steps (parked, in order)

1. Anchored A/B harness: port ml-knowledgegraph gold.jsonl into
   gliformer_eval CrossNER format; measure base×large × plain×composite
   labels × thresholds × propagation on/off.
2. v0.1.4: config pre-fill (gazetteer + schema defaults + aliases).
3. Ship winners, record measured deltas here.
