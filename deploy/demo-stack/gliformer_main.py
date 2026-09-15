"""GLiFormer backend for the veld-extractors wire contract.

Speaks the SAME /health + /ner + /extract contract as the gliner2 bundle
(ml-knowledgegraph/veld-extractors/services/main.py), so the Rust
VeldExtractorsAdapter (and anything else pointed at the bundle) can swap
this in by changing the base URL — zero client changes.

Backend: knowledgator/gliformer-base-v1 (released 2026-09-11) — one encoder
for NER + joint relation extraction + classification. NOT a fastino
gliner2.x successor; it is Knowledgator's line (same house as gliner-relex).

Verified on strix CPU (2026-09-15, /tmp/gliformer-demo): NER 94 ms,
joint relations 64 ms, 0.99+ entity scores on the standard smoke text.

Env:
    GLIFORMER_MODEL  — model id or local dir (default gliformer-base-v1)
    GLIFORMER_DEVICE — cpu | cuda (default cpu; demo VMs have no GPU)
"""

from __future__ import annotations

import os
import threading
import time
from typing import Optional

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

MODEL_ID = os.environ.get(
    "GLIFORMER_MODEL", "knowledgator/gliformer-base-v1"
)
DEVICE = os.environ.get("GLIFORMER_DEVICE", "cpu")
BACKEND = "gliformer"

# Input bound: gliformer SILENTLY returns zero entities past its ~4-5k
# effective-token ceiling (measured 2026-09-15, 8k-char probe —
# gliformer-service/VERDICT.md). Fail LOUD with 413 instead of lying
# with an empty result. Chunk at the caller (veld's markdown chunker
# already does).
MAX_TEXT_CHARS = 4000

app = FastAPI(title="veld-extractors-gliformer")

_model = None
_lock = threading.Lock()


def _load_model():
    """Lazy singleton — container may serve /health before weights load."""
    global _model
    if _model is None:
        with _lock:
            if _model is None:
                from gliformer import GLiFormer

                m = GLiFormer.from_pretrained(MODEL_ID, load_tokenizer=True)
                _model = m.to(DEVICE).eval()
    return _model


# --- wire contract (mirrors _contract.py in ml-knowledgegraph) ------------

class NerRequest(BaseModel):
    text: str
    labels: list[str]
    threshold: float = 0.5


class NerEntity(BaseModel):
    text: str
    label: str
    start: int
    end: int
    score: float


class NerResponse(BaseModel):
    entities: list[NerEntity]
    model: str
    backend: str
    elapsed_ms: int


class ExtractRequest(BaseModel):
    text: str
    entities: dict[str, str]  # label -> description (gliformer keys on label)
    relations: Optional[dict[str, str]] = None
    threshold: float = 0.5
    entity_threshold: Optional[float] = None
    relation_threshold: Optional[float] = None


class ExtractResponse(BaseModel):
    entities: dict[str, list[dict]]
    relations: dict[str, list[dict]]
    model: str
    backend: str
    elapsed_ms: int


# --- endpoints -------------------------------------------------------------


@app.get("/health")
def health() -> dict:
    loaded = _model is not None
    return {
        "status": "ok",
        "backend": BACKEND,
        "model": MODEL_ID,
        "device": DEVICE,
        "model_loaded": loaded,
    }


def _check_bounds(text: str) -> None:
    if len(text) > MAX_TEXT_CHARS:
        raise HTTPException(
            413,
            f"text is {len(text)} chars > {MAX_TEXT_CHARS}; GLiFormer "
            "silently drops entities beyond its ~4-5k token ceiling — "
            "chunk the input (see gliformer-service/VERDICT.md)",
        )


@app.post("/ner", response_model=NerResponse)
def ner(req: NerRequest) -> NerResponse:
    _check_bounds(req.text)
    model = _load_model()
    t0 = time.perf_counter()
    raw = model.predict_entities(req.text, req.labels, threshold=req.threshold)
    elapsed_ms = int((time.perf_counter() - t0) * 1000)
    ents = [
        NerEntity(
            text=e["text"],
            label=e["label"],
            start=e["start"],
            end=e["end"],
            score=e["score"],
        )
        for e in raw
        if e["text"]
    ]
    return NerResponse(
        entities=ents, model=MODEL_ID, backend=BACKEND, elapsed_ms=elapsed_ms
    )


@app.post("/extract", response_model=ExtractResponse)
def extract(req: ExtractRequest) -> ExtractResponse:
    _check_bounds(req.text)
    model = _load_model()
    et = req.entity_threshold if req.entity_threshold is not None else req.threshold
    rt = req.relation_threshold if req.relation_threshold is not None else req.threshold
    labels = list(req.entities.keys())

    t0 = time.perf_counter()
    if req.relations:
        # Joint pass: entities + relations in one inference call. Generate
        # candidates at the lower of the two bars so neither task is starved,
        # then post-filter each by its own floor (same semantics as main.py).
        gen_thr = min(et, rt)
        results = model.inference(
            req.text,
            joint_relations={
                "veld": {
                    "entities": labels,
                    "relations": list(req.relations.keys()),
                }
            },
            threshold=gen_thr,
        )
        # Verified live 2026-09-15: joint inference() returns keys
        # ['ner', 'joint_relex'] — each a list with ONE entry per input
        # text; we always send a single text, so take [0].
        raw_ents = (results.get("ner") or [[]])[0] or []
        raw_rels = (results.get("joint_relex") or [{}])[0] or []
    else:
        raw_ents = model.predict_entities(req.text, labels, threshold=et)
        raw_rels = []

    ents_out: dict[str, list[dict]] = {lab: [] for lab in labels}
    for e in raw_ents:
        if not e["text"] or e["score"] < et:
            continue
        ents_out.setdefault(e["label"], []).append(
            {
                "text": e["text"],
                "start": e["start"],
                "end": e["end"],
                "score": round(float(e["score"]), 4),
            }
        )

    rels_out: dict[str, list[dict]] = {k: [] for k in (req.relations or {})}
    for r in raw_rels:
        score = float(r.get("score", 0.0))
        if score < rt:
            continue
        rels_out.setdefault(r["relation"], []).append(
            {
                "head": r["head"]["text"],
                "tail": r["tail"]["text"],
                "relation": r["relation"],
                "score": round(score, 4),
            }
        )

    elapsed_ms = int((time.perf_counter() - t0) * 1000)
    return ExtractResponse(
        entities=ents_out,
        relations=rels_out,
        model=MODEL_ID,
        backend=BACKEND,
        elapsed_ms=elapsed_ms,
    )
