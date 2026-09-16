"""GLiFormer backend for the veld-extractors wire contract.

Speaks the SAME /health + /ner + /extract contract as the gliner2 bundle
(ml-knowledgegraph/veld-extractors/services/main.py), so the Rust
VeldExtractorsAdapter (and anything else pointed at the bundle) can swap
this in by changing the base URL — zero client changes.

Backend: knowledgator/gliformer-base-v1 (released 2026-09-11) — one encoder
for NER + joint relation extraction + classification. NOT a fastino
gliner2.x successor; it is Knowledgator's line (same house as gliner-relex).

Verified on strix CPU (2026-09-15): NER 94 ms, joint relations 64 ms,
0.99+ entity scores on the standard smoke text.

Long input: GLiFormer SILENTLY returns zero entities past its ~4-5k
effective-token ceiling (measured 2026-09-15, 8k-char probe —
gliformer-service/VERDICT.md). This service CHUNKS long documents itself
(sentence-aligned, with overlap so boundary-spanning entities survive),
remaps offsets to DOCUMENT coordinates, and dedupes. 413 only beyond a
hard 50k-char abuse ceiling.

Env:
    GLIFORMER_MODEL  — model id or local dir (default gliformer-base-v1)
    GLIFORMER_DEVICE — cpu | cuda (default cpu; demo VMs have no GPU)
"""

from __future__ import annotations

import ipaddress
import os
import re
import socket
import threading
import time
from typing import Optional
from urllib.parse import urlparse

import urllib.request

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, StreamingResponse
from pydantic import BaseModel

MODEL_ID = os.environ.get(
    "GLIFORMER_MODEL", "knowledgator/gliformer-base-v1"
)
DEVICE = os.environ.get("GLIFORMER_DEVICE", "cpu")
BACKEND = "gliformer"

# --- input sizing -----------------------------------------------------------
# Single-pass limit (safe under the ~4-5k effective-token ceiling).
MAX_TEXT_CHARS = 4000
# Long docs are chunked to CHUNK_TARGET chars with up to CHUNK_OVERLAP
# sentences repeated across the seam, so an entity/relation that straddles
# a boundary appears whole in at least one chunk.
CHUNK_TARGET = 3200
CHUNK_OVERLAP = 2
# Hard ceiling: ~16 chunks ≈ 10-15 s CPU — beyond this, refuse (413).
MAX_DOC_CHARS = 50000

app = FastAPI(title="veld-extractors-gliformer")

import otel_setup  # noqa: E402  (local module, after app creation)

otel_setup.init_otel(app)

_model = None
_lock = threading.Lock()


def _load_model():
    """Lazy singleton — container may serve /health before weights load."""
    global _model
    if _model is None:
        with _lock:
            if _model is None:
                from gliformer import GLiFormer

                with otel_setup.tracer().start_as_current_span(
                    "gliformer.model_load"
                ) as span:
                    span.set_attribute("model", MODEL_ID)
                    span.set_attribute("device", DEVICE)
                    t0 = time.perf_counter()
                    m = GLiFormer.from_pretrained(MODEL_ID, load_tokenizer=True)
                    _model = m.to(DEVICE).eval()
                    span.set_attribute("load_ms", int((time.perf_counter() - t0) * 1000))
    return _model


# --- chunker ----------------------------------------------------------------

_SENTENCE_RE = re.compile(r"[^.!?]*[.!?]+(?:\s+|$)|[^.!?]+")


def _hard_split(sentence: str, target: int) -> list[str]:
    """A single sentence longer than target: split on word boundaries."""
    parts, cur = [], ""
    for word in sentence.split(" "):
        if cur and len(cur) + 1 + len(word) > target:
            parts.append(cur)
            cur = word
        else:
            cur = f"{cur} {word}".strip()
    if cur:
        parts.append(cur)
    return parts


def chunk_text(
    text: str, target: int = CHUNK_TARGET, overlap: int = CHUNK_OVERLAP
) -> list[tuple[int, str]]:
    """Sentence-aligned chunking with absolute offsets.

    Returns [(absolute_offset, chunk_text)]. Because each chunk's text is a
    verbatim contiguous slice of the input starting exactly at its offset,
    document-global entity offsets are chunk_offset + local_offset — exact,
    no drift. Overlap sentences are repeated across seams so entities
    straddling a boundary appear whole in at least one chunk (a relation
    whose head and tail fall in DIFFERENT non-overlapped regions is still
    lost — that is the honest limit of local chunking).
    """
    # sentences with absolute offsets (regex keeps trailing whitespace so
    # concatenation reproduces the input exactly)
    spans: list[tuple[int, str]] = []
    for m in _SENTENCE_RE.finditer(text):
        if m.group(0).strip():
            spans.append((m.start(), m.group(0)))

    # hard-split pathologically long sentences; recompute offsets by find()
    # and absorb any inter-span whitespace into the FOLLOWING span VERBATIM
    # (hard_split's .strip() drops boundary spaces; whitespace-only sentence
    # matches are skipped) — without absorption the packing stage sees GAPS
    # and coverage/offset remapping break.
    expanded: list[tuple[int, str]] = []
    prev_end: Optional[int] = None

    def _push(start: int, end: int) -> None:
        nonlocal prev_end
        if prev_end is not None and start > prev_end:
            start = prev_end  # absorb gap whitespace, keep slice verbatim
        expanded.append((start, text[start:end]))
        prev_end = end

    for off, s in spans:
        if len(s) <= target:
            _push(off, off + len(s))
        else:
            cursor = off
            for part in _hard_split(s, target):
                idx = text.find(part, cursor)
                if idx == -1:  # pragma: no cover — defensive
                    break
                _push(idx, idx + len(part))
                cursor = idx + len(part)

    # greedy packing with seam overlap
    chunks: list[tuple[int, str]] = []
    cur: list[tuple[int, str]] = []
    cur_len = 0
    for off, s in expanded:
        if cur and cur_len + len(s) > target:
            chunks.append((cur[0][0], "".join(x[1] for x in cur)))
            back: list[tuple[int, str]] = []
            blen = 0
            for poff, ps in reversed(cur):
                if len(back) >= overlap or blen + len(ps) > target // 4:
                    break
                back.insert(0, (poff, ps))
                blen += len(ps)
            cur, cur_len = back, blen
        cur.append((off, s))
        cur_len += len(s)
    if cur:
        chunks.append((cur[0][0], "".join(x[1] for x in cur)))
    return chunks


def _doc_chunks(text: str) -> list[tuple[int, str]]:
    if len(text) <= MAX_TEXT_CHARS:
        return [(0, text)]
    return chunk_text(text)


# --- progressive context (build-up across chunks) ---------------------------
# Entities found in chunks 1..N-1 inform chunk N: known entity strings are
# re-matched (word-boundary, case-sensitive) in every later chunk, so a
# later mention the MODEL misses (weak local context, no cue words) is still
# captured with exact offsets. After all chunks, a consolidation sweep
# back-fills the WHOLE document with the final vocabulary. Model-found
# always wins over propagated on dedup (propagation never overwrites model
# scores). NOTE: gliformer 0.1.2 has NO few-shot exemplar API (verified from
# source — `entities` dict = named label groups, not examples), so this is
# deterministic string propagation, not model-side anchoring. Relations
# remain model-only.


class EntityVocab:
    """Exact-string, word-boundary, case-sensitive entity memory."""

    def __init__(self) -> None:
        self._strings: dict[str, set[str]] = {}  # label -> {exact strings}
        self._regex: dict[str, list[tuple[str, "re.Pattern[str]"]]] = {}

    def add(self, text: str, label: str) -> None:
        if not text or len(text.strip()) < 2:
            return
        strs = self._strings.setdefault(label, set())
        if text not in strs:
            strs.add(text)
            pat = re.compile(r"\b" + re.escape(text) + r"\b")
            self._regex.setdefault(label, []).append((text, pat))

    def match_in(self, text: str) -> list[dict]:
        """All known-entity mentions in text -> dicts with offsets."""
        out = []
        for label, pats in self._regex.items():
            for s, pat in pats:
                for m in pat.finditer(text):
                    out.append(
                        {
                            "text": text[m.start():m.end()],
                            "label": label,
                            "start": m.start(),
                            "end": m.end(),
                            "score": 1.0,
                            "source": "propagated",
                        }
                    )
        return out


def _merge_chunk(
    results: dict[tuple, dict],
    found: list[dict],
    base: int,
    chunk_text: str,
    vocab: "EntityVocab | None",
    propagate: bool,
) -> int:
    """Merge one chunk's entities into results; returns propagated count.

    Model-found entities take precedence; propagated mentions only fill
    offsets the model did not produce. Everything the model found is added
    to the vocabulary for later chunks.
    """
    propagated = 0
    for e in found:
        if not e["text"]:
            continue
        key = (e["label"], base + e["start"], base + e["end"])
        cur = results.get(key)
        if cur is None or e["score"] > cur["score"]:
            results[key] = {
                **e,
                "start": base + e["start"],
                "end": base + e["end"],
                "source": "model",
            }
        if vocab is not None:
            vocab.add(e["text"], e["label"])
    if propagate and vocab is not None:
        for e in vocab.match_in(chunk_text):
            key = (e["label"], base + e["start"], base + e["end"])
            if key not in results:  # never overwrite model-found
                results[key] = {
                    **e,
                    "start": base + e["start"],
                    "end": base + e["end"],
                }
                propagated += 1
    return propagated


# --- domain bootstrap (known-context schema from a website) -----------------
# User gives a domain/URL → we fetch the site, run a GENERIC probe-label NER
# pass over its text, and return: (a) the probe labels that actually FIRE on
# this domain (dropping dead labels cuts label noise), each enriched with
# real example strings from THEIR site (GLiNER-family labels are free text —
# examples in the label string sharpen conditioning), (b) seed entity strings
# for the progressive-context gazetteer, (c) relation suggestions from the
# fired label pairs. This is the QUALITY-LEVERS.md "config pre-fill" design,
# driven by the client's own website instead of a hand-written config.

PROBE_LABELS = [
    "product", "service", "organization", "person", "location",
    "technology", "process", "role", "event", "document",
]

_RELATION_TEMPLATES = {
    frozenset({"organization", "product"}): ("offers", "an organization offers a product"),
    frozenset({"organization", "service"}): ("provides", "an organization provides a service"),
    frozenset({"person", "organization"}): ("works_at", "a person is employed by an organization"),
    frozenset({"person", "role"}): ("has_role", "a person holds a role"),
    frozenset({"organization", "technology"}): ("uses", "an organization uses a technology"),
    frozenset({"process", "technology"}): ("uses", "a process uses a technology"),
    frozenset({"location", "organization"}): ("located_in", "an organization is located in a place"),
    frozenset({"event", "organization"}): ("hosts", "an organization hosts an event"),
}

_FETCH_LIMIT = 500_000  # bytes
_FETCH_TIMEOUT = 8


def _fetch_site(domain_or_url: str) -> tuple[str, str, str]:
    """Fetch a website's visible text. Returns (url, title, text).
    http/https only, private/loopback targets rejected (SSRF guard)."""
    raw = domain_or_url.strip()
    if not urlparse(raw).scheme:
        raw = "https://" + raw
    p = urlparse(raw)
    if p.scheme not in ("http", "https") or not p.hostname:
        raise HTTPException(400, f"unsupported URL: {domain_or_url!r}")
    try:
        infos = socket.getaddrinfo(p.hostname, None)
    except OSError as e:
        raise HTTPException(400, f"cannot resolve {p.hostname}: {e}")
    for info in infos:
        ip = ipaddress.ip_address(info[4][0])
        if ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_reserved:
            raise HTTPException(400, "refusing to fetch private/loopback targets")
    req = urllib.request.Request(
        raw, headers={"User-Agent": "AgentFolio-demo-stack/0.1.6 (+extraction bootstrap)"}
    )
    try:
        with urllib.request.urlopen(req, timeout=_FETCH_TIMEOUT) as r:
            html = r.read(_FETCH_LIMIT).decode("utf-8", errors="replace")
            final_url = r.geturl()
    except Exception as e:
        raise HTTPException(502, f"fetch failed for {raw}: {e}")
    title_m = re.search(r"<title[^>]*>(.*?)</title>", html, re.I | re.S)
    title = (title_m.group(1).strip() if title_m else p.hostname)[:120]
    # strip scripts/styles/tags — visible text only
    text = re.sub(r"(?is)<(script|style|noscript|svg|header|footer|nav)[^>]*>.*?</\1>", " ", html)
    text = re.sub(r"(?s)<[^>]+>", " ", text)
    text = re.sub(r"\s+", " ", re.sub(r"&[a-z#0-9]+;", " ", text)).strip()
    return final_url, title, text


def _bootstrap_schema(url: str) -> dict:
    final_url, title, text = _fetch_site(url)
    if len(text) < 200:
        raise HTTPException(
            422, f"site text too thin ({len(text)} chars) to bootstrap from"
        )
    model = _load_model()
    # probe pass over the first chunks of site text (generic labels)
    fired: dict[str, list[str]] = {}
    for base, chunk in _doc_chunks(text)[:4]:
        for e in model.predict_entities(chunk, PROBE_LABELS, threshold=0.5):
            t = e["text"].strip()
            if 2 < len(t) < 60 and e["score"] >= 0.5:
                fired.setdefault(e["label"], [])
                if t not in fired[e["label"]] and len(fired[e["label"]]) < 12:
                    fired[e["label"]].append(t)

    entities: dict[str, str] = {}
    for label in PROBE_LABELS:
        if label not in fired:
            continue  # dead label on this domain — drop it (less label noise)
        examples = ", ".join(fired[label][:4])
        entities[label] = f"a {label} (e.g. from this domain: {examples})"

    seeds: dict[str, list[str]] = {}
    for label, strings in fired.items():
        keep = [s for s in strings if len(s) >= 4][:6]
        if keep:
            seeds[label] = keep
    # the site title's distinctive words are the strongest org seeds
    title_words = [w for w in re.findall(r"[A-Z][\w&-]{3,}", title) if w.lower() not in
                   {"the", "and", "voor", "met", "home", "page", "welcome"}]
    if title_words:
        seeds.setdefault("organization", [])
        for w in title_words[:3]:
            if w not in seeds["organization"]:
                seeds["organization"].append(w)

    relations: dict[str, str] = {}
    for pair, (rel, desc) in _RELATION_TEMPLATES.items():
        if pair <= set(fired):
            relations[rel] = desc

    return {
        "fetched_url": final_url,
        "title": title,
        "site_chars": len(text),
        "entities": entities,
        "relations": relations,
        "seed_entities": seeds,
        "note": "labels conditioned on YOUR site's vocabulary — edit freely; "
                "seeds are exact-match gazetteer entries guaranteed found",
    }


def _bootstrap_schema_stages(url: str):
    """Generator version of _bootstrap_schema: yields SSE stage events so
    the UI can show REAL progress (fetch → chars → model load → probe i/n
    → done) instead of a static 'working…' line."""
    import json as _json

    def ev(obj: dict) -> str:
        return f"data: {_json.dumps(obj)}\n\n"

    yield ev({"stage": "fetch", "url": url, "msg": "fetching site…"})
    final_url, title, text = _fetch_site(url)
    if len(text) < 200:
        raise HTTPException(
            422, f"site text too thin ({len(text)} chars) to bootstrap from"
        )
    yield ev({
        "stage": "fetched", "msg": f"site fetched — {len(text)} chars",
        "title": title, "chars": len(text),
    })
    if _model is None:
        yield ev({"stage": "model_load",
                  "msg": "loading extraction model (first call, ~30-60s)…"})
    model = _load_model()
    if _model is not None:
        yield ev({"stage": "model_ready", "msg": "model ready"})

    fired: dict[str, list[str]] = {}
    chunks = _doc_chunks(text)[:4]
    for i, (base, chunk) in enumerate(chunks, 1):
        yield ev({"stage": "probe", "i": i, "n": len(chunks),
                  "msg": f"probing labels on site text ({i}/{len(chunks)})…"})
        for e in model.predict_entities(chunk, PROBE_LABELS, threshold=0.5):
            t = e["text"].strip()
            if 2 < len(t) < 60 and e["score"] >= 0.5:
                fired.setdefault(e["label"], [])
                if t not in fired[e["label"]] and len(fired[e["label"]]) < 12:
                    fired[e["label"]].append(t)

    entities: dict[str, str] = {}
    for label in PROBE_LABELS:
        if label not in fired:
            continue
        examples = ", ".join(fired[label][:4])
        entities[label] = f"a {label} (e.g. from this domain: {examples})"

    seeds: dict[str, list[str]] = {}
    for label, strings in fired.items():
        keep = [s for s in strings if len(s) >= 4][:6]
        if keep:
            seeds[label] = keep
    title_words = [w for w in re.findall(r"[A-Z][\w&-]{3,}", title) if w.lower() not in
                   {"the", "and", "voor", "met", "home", "page", "welcome"}]
    if title_words:
        seeds.setdefault("organization", [])
        for w in title_words[:3]:
            if w not in seeds["organization"]:
                seeds["organization"].append(w)

    relations: dict[str, str] = {}
    for pair, (rel, desc) in _RELATION_TEMPLATES.items():
        if pair <= set(fired):
            relations[rel] = desc

    yield ev({
        "stage": "done",
        "msg": f"{len(entities)} labels kept, {len(relations)} relations, "
               f"{sum(len(v) for v in seeds.values())} seed entities",
        "fetched_url": final_url, "title": title, "site_chars": len(text),
        "entities": entities, "relations": relations, "seed_entities": seeds,
    })


# --- wire contract (mirrors _contract.py in ml-knowledgegraph) --------------

class BootstrapRequest(BaseModel):
    url: str  # domain or full URL — the client's website


class NerRequest(BaseModel):
    text: str
    labels: list[str]
    threshold: float = 0.5
    # Progressive context: entities found in chunks 1..N-1 are re-matched
    # in later chunks + a final full-doc sweep (see EntityVocab).
    propagate: bool = True
    # Known-context gazetteer (QUALITY-LEVERS config pre-fill): label ->
    # exact strings guaranteed found from the first chunk. Typically the
    # output of /schema/bootstrap.
    seed_entities: Optional[dict[str, list[str]]] = None


class NerEntity(BaseModel):
    text: str
    label: str
    start: int  # DOCUMENT-global offset (chunked input remapped)
    end: int
    score: float
    source: Optional[str] = None  # "model" (default) | "propagated"


class NerResponse(BaseModel):
    entities: list[NerEntity]
    model: str
    backend: str
    elapsed_ms: int
    chunks: Optional[int] = None  # >1 when the input was chunked
    propagated: Optional[int] = None  # mentions found via earlier chunks


class ExtractRequest(BaseModel):
    text: str
    entities: dict[str, str]  # label -> description (gliformer keys on label)
    relations: Optional[dict[str, str]] = None
    threshold: float = 0.5
    entity_threshold: Optional[float] = None
    relation_threshold: Optional[float] = None
    # Progressive context: entities found in chunks 1..N-1 inform chunk N
    # and a final full-doc sweep back-fills everything (relations stay
    # model-only — see EntityVocab docstring).
    propagate: bool = True
    # Known-context gazetteer seeds (from /schema/bootstrap): label ->
    # exact strings, present in results from the first chunk.
    seed_entities: Optional[dict[str, list[str]]] = None


class ExtractResponse(BaseModel):
    entities: dict[str, list[dict]]
    relations: dict[str, list[dict]]
    model: str
    backend: str
    elapsed_ms: int
    chunks: Optional[int] = None  # >1 when the input was chunked
    propagated: Optional[int] = None  # entity mentions via earlier chunks


# --- endpoints -------------------------------------------------------------


@app.get("/", include_in_schema=False)
def demo_page() -> FileResponse:
    """Client-facing demo UI: paste a doc, edit the schema, see entities
    highlighted (model vs propagated), relations, chunk/latency stats.
    Served same-origin so fetch('/extract') needs no CORS."""
    return FileResponse(
        os.path.join(os.path.dirname(__file__), "demo.html"),
        media_type="text/html",
    )


@app.get("/local", include_in_schema=False)
def local_chat_page() -> FileResponse:
    """LFM2.5-350M chat running ENTIRELY client-side (transformers.js,
    WebGPU with WASM fallback, ~294 MB int4 ONNX cached by the browser).
    The server only serves this static file — inference is on-device."""
    return FileResponse(
        os.path.join(os.path.dirname(__file__), "local-chat.html"),
        media_type="text/html",
    )


@app.get("/health")
def health() -> dict:
    loaded = _model is not None
    return {
        "status": "ok",
        "backend": BACKEND,
        "model": MODEL_ID,
        "device": DEVICE,
        "model_loaded": loaded,
        "chunking": {
            "single_pass_max_chars": MAX_TEXT_CHARS,
            "chunk_target": CHUNK_TARGET,
            "overlap_sentences": CHUNK_OVERLAP,
            "hard_doc_limit": MAX_DOC_CHARS,
        },
    }


def _check_bounds(text: str) -> None:
    if len(text) > MAX_DOC_CHARS:
        raise HTTPException(
            413,
            f"document is {len(text)} chars > hard limit {MAX_DOC_CHARS} "
            f"(≈{MAX_DOC_CHARS // CHUNK_TARGET + 1} chunks ≈ 10-15 s CPU on "
            "a demo VM); split the request",
        )


@app.post("/schema/bootstrap")
def schema_bootstrap(req: BootstrapRequest):
    """SSE stream of REAL bootstrap stages: fetch → chars fetched → model
    load (only if cold) → probe i/n per chunk → done (full schema payload
    in the final event). Consumed by the demo UI's live progress line."""
    return StreamingResponse(
        _bootstrap_schema_stages(req.url), media_type="text/event-stream"
    )


# --- browser-model files served SAME-ORIGIN (fast repeat loads) -------------
# transformers.js can be pointed at any host speaking the HF layout. We bake
# the ONNX weights into the image and serve them from our own origin with
# immutable cache headers — repeat /local loads read the browser disk cache,
# zero HuggingFace round-trips, no CORS preflights.

_MODELS_DIR = os.environ.get("GLIFORMER_MODELS_DIR", "/models/hf")
_ASSETS_DIR = os.environ.get("GLIFORMER_ASSETS_DIR", "/assets")


@app.middleware("http")
async def _immutable_model_cache(request, call_next):
    resp = await call_next(request)
    if request.url.path.startswith(("/models/", "/assets/")):
        resp.headers["Cache-Control"] = "public, max-age=31536000, immutable"
        resp.headers["Access-Control-Allow-Origin"] = "*"
    return resp


def _serve_static(base_env: str, rest: str) -> FileResponse:
    base = os.environ.get(base_env, _MODELS_DIR if "MODELS" in base_env else _ASSETS_DIR)
    path = os.path.realpath(os.path.join(base, rest))
    if not path.startswith(os.path.realpath(base)) or not os.path.isfile(path):
        raise HTTPException(404, f"no such file: {rest}")
    return FileResponse(path)


@app.api_route("/models/{rest:path}", methods=["GET", "HEAD"], include_in_schema=False)
def model_file(rest: str) -> FileResponse:
    span = otel_setup.tracer().start_span("gliformer.model_file")
    try:
        path = os.path.realpath(os.path.join(_MODELS_DIR, rest))
        if os.path.isfile(path):
            span.set_attribute("bytes", os.path.getsize(path))
        span.set_attribute("file", rest[-80:])
        return _serve_static("GLIFORMER_MODELS_DIR", rest)
    finally:
        span.end()


@app.api_route("/assets/{rest:path}", methods=["GET", "HEAD"], include_in_schema=False)
def asset_file(rest: str) -> FileResponse:
    return _serve_static("GLIFORMER_ASSETS_DIR", rest)


class ClientTelemetry(BaseModel):
    event: str                      # e.g. load_failed, gen_failed, extract_failed
    backend: Optional[str] = None   # webgpu | wasm | null
    stage: Optional[str] = None     # import | probe | fetch | session | generate
    error: Optional[str] = None
    url: Optional[str] = None
    model: Optional[str] = None


@app.post("/telemetry")
def telemetry(req: ClientTelemetry, request: "Request") -> dict:
    """Client-side failure beacon → OTel span (device breakage becomes
    queryable: iOS content blockers, OOM aborts, offline imports)."""
    otel_setup.client_event(
        req.event,
        {
            "backend": req.backend, "stage": req.stage, "error": req.error,
            "url": req.url, "model": req.model,
            "ua": request.headers.get("user-agent", ""),
        },
    )
    return {"ok": True}


@app.post("/ner", response_model=NerResponse)
def ner(req: NerRequest) -> NerResponse:
    _check_bounds(req.text)
    model = _load_model()
    t0 = time.perf_counter()

    best: dict[tuple, dict] = {}
    chunks = _doc_chunks(req.text)
    vocab = EntityVocab() if len(chunks) > 1 or req.seed_entities else None
    propagated = 0
    if vocab is not None and req.seed_entities:
        for label, strings in req.seed_entities.items():
            if label not in req.labels:
                continue  # only seed labels the caller actually requested
            for s in strings:
                vocab.add(s, label)
    for base, ctext in chunks:
        raw = model.predict_entities(
            ctext, req.labels, threshold=req.threshold
        )
        propagated += _merge_chunk(
            best, raw, base, ctext, vocab, req.propagate
        )

    # Consolidation sweep: the final vocabulary back-fills the WHOLE doc —
    # an entity the model first surfaced in a LATE chunk also fills its
    # earlier mentions. Never overwrites model-found keys.
    if vocab is not None and req.propagate:
        for e in vocab.match_in(req.text):
            key = (e["label"], e["start"], e["end"])
            if key not in best:
                best[key] = e
                propagated += 1

    ents = [NerEntity(**best[k]) for k in sorted(best, key=lambda k: k[1])]
    elapsed_ms = int((time.perf_counter() - t0) * 1000)
    return NerResponse(
        entities=ents,
        model=MODEL_ID,
        backend=BACKEND,
        elapsed_ms=elapsed_ms,
        chunks=len(chunks) if len(chunks) > 1 else None,
        propagated=propagated if (len(chunks) > 1 or req.seed_entities) else None,
    )


@app.post("/extract", response_model=ExtractResponse)
def extract(req: ExtractRequest) -> ExtractResponse:
    _check_bounds(req.text)
    model = _load_model()
    et = req.entity_threshold if req.entity_threshold is not None else req.threshold
    rt = req.relation_threshold if req.relation_threshold is not None else req.threshold
    labels = list(req.entities.keys())

    t0 = time.perf_counter()
    ent_best: dict[tuple, dict] = {}
    rel_best: dict[tuple, dict] = {}
    chunks = _doc_chunks(req.text)
    vocab = EntityVocab() if len(chunks) > 1 or req.seed_entities else None
    propagated = 0
    if vocab is not None and req.seed_entities:
        for label, strings in req.seed_entities.items():
            if label not in labels:
                continue  # only seed labels the caller actually requested
            for s in strings:
                vocab.add(s, label)

    for base, ctext in chunks:
        if req.relations:
            # Joint pass: entities + relations in one inference call.
            # Generate candidates at the lower of the two bars so neither
            # task is starved, then post-filter each by its own floor
            # (same semantics as main.py).
            gen_thr = min(et, rt)
            results = model.inference(
                ctext,
                joint_relations={
                    "veld": {
                        "entities": labels,
                        "relations": list(req.relations.keys()),
                    }
                },
                threshold=gen_thr,
            )
            # Verified live 2026-09-15: joint inference() returns keys
            # ['ner', 'joint_relex'] — each a list with ONE entry per
            # input text; we always send a single chunk, so take [0].
            raw_ents = (results.get("ner") or [[]])[0] or []
            raw_rels = (results.get("joint_relex") or [{}])[0] or []
        else:
            raw_ents = model.predict_entities(ctext, labels, threshold=et)
            raw_rels = []

        # entity threshold floor + progressive-context merge
        raw_ents = [e for e in raw_ents if e.get("score", 0) >= et]
        propagated += _merge_chunk(
            ent_best, raw_ents, base, ctext, vocab, req.propagate
        )

        for r in raw_rels:
            score = float(r.get("score", 0.0))
            if score < rt:
                continue
            key = (r["head"]["text"], r["relation"], r["tail"]["text"])
            if key not in rel_best or score > rel_best[key]["score"]:
                rel_best[key] = {
                    "head": r["head"]["text"],
                    "tail": r["tail"]["text"],
                    "relation": r["relation"],
                    "score": round(score, 4),
                }

    # Consolidation sweep (entities only; relations stay model-only).
    if vocab is not None and req.propagate:
        for e in vocab.match_in(req.text):
            key = (e["label"], e["start"], e["end"])
            if key not in ent_best:
                ent_best[key] = e
                propagated += 1

    ents_out: dict[str, list[dict]] = {lab: [] for lab in labels}
    for key in sorted(ent_best, key=lambda k: k[1]):
        e = ent_best[key]
        ents_out.setdefault(e["label"], []).append(e)

    rels_out: dict[str, list[dict]] = {k: [] for k in (req.relations or {})}
    for r in rel_best.values():
        rels_out.setdefault(r["relation"], []).append(r)

    elapsed_ms = int((time.perf_counter() - t0) * 1000)
    span = otel_setup.tracer().start_span("gliformer.extract")
    span.set_attribute("doc_chars", len(req.text))
    span.set_attribute("chunks", len(chunks))
    span.set_attribute("propagated", propagated)
    span.set_attribute("seeded", bool(req.seed_entities))
    span.set_attribute("n_entities", len(ent_best))
    span.set_attribute("n_relations", len(rel_best))
    span.set_attribute("elapsed_ms", elapsed_ms)
    span.end()
    return ExtractResponse(
        entities=ents_out,
        relations=rels_out,
        model=MODEL_ID,
        backend=BACKEND,
        elapsed_ms=elapsed_ms,
        chunks=len(chunks) if len(chunks) > 1 else None,
        propagated=propagated if (len(chunks) > 1 or req.seed_entities) else None,
    )
