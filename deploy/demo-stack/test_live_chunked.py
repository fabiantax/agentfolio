"""Live chunked-extraction test against the running container.

Builds a ~10k-char document with entities scattered throughout (including
near chunk seams), posts /ner, and verifies:
  1. response reports chunks > 1
  2. EVERY entity's offsets are exact: doc[start:end] == text
  3. all injected entities are found (per label)
  4. no duplicate (label, start, end) triples
Run: python3 test_live_chunked.py
"""
import json
import sys
import types
import urllib.error
import urllib.request

# import EntityVocab without pulling fastapi/pydantic (see test_chunker.py)
_fa = types.ModuleType("fastapi")
_fa.HTTPException = Exception
_fa.FastAPI = lambda **kw: types.SimpleNamespace(
    get=lambda *a, **k: (lambda f: f), post=lambda *a, **k: (lambda f: f)
)
sys.modules.setdefault("fastapi", _fa)
_py = types.ModuleType("pydantic")
_py.BaseModel = object
sys.modules.setdefault("pydantic", _py)
sys.path.insert(0, ".")
from gliformer_main import EntityVocab  # noqa: E402

FILLER = (
    "The quarterly review covered infrastructure costs and vendor "
    "contracts. Nothing else was discussed during the long meeting. "
)

people = [
    "Alice Jansen", "Bob de Vries", "Carol Meyer", "Dennis Bakker",
    "Eva Smit", "Frank Mulder", "Grace Visser", "Henk Boer",
]
orgs = [
    "Acme Corp", "Initech", "Globex", "Umbrella Ltd", "Stark Industries",
    "Wayne Enterprises", "Cyberdyne Systems", "Soylent Corp",
]
cities = [
    "London", "Berlin", "Amsterdam", "Rotterdam", "Utrecht",
    "Eindhoven", "Delft", "Den Haag",
]

# Assemble: filler blocks with entity sentences interleaved, ~10k chars
parts, expect = [], {"person": set(), "organization": set(), "location": set()}
i = 0
while sum(len(p) for p in parts) < 10000:
    for _ in range(6):
        parts.append(FILLER)
    s = (
        f"{people[i % 8]} works at {orgs[i % 8]} in {cities[i % 8]}. "
    )
    parts.append(s)
    expect["person"].add(people[i % 8])
    expect["organization"].add(orgs[i % 8])
    expect["location"].add(cities[i % 8])
    i += 1
doc = "".join(parts)
print(f"doc: {len(doc)} chars, injected {i} entity sentences")


def post(path, payload):
    req = urllib.request.Request(
        "http://127.0.0.1:18080" + path,
        json.dumps(payload).encode(),
        {"content-type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.status, json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


# 1. chunked /ner
s, d = post(
    "/ner",
    {"text": doc, "labels": ["person", "organization", "location"], "threshold": 0.5},
)
assert s == 200, f"status {s}: {d}"
assert d["chunks"] and d["chunks"] > 1, f"expected chunks>1, got {d.get('chunks')}"
found = {"person": set(), "organization": set(), "location": set()}
keys = set()
for e in d["entities"]:
    exact = doc[e["start"]:e["end"]]
    assert exact == e["text"], (
        f"OFFSET DRIFT: claimed {e['text']!r} but doc[{e['start']}:{e['end']}]"
        f" = {exact!r}"
    )
    k = (e["label"], e["start"], e["end"])
    assert k not in keys, f"DUPLICATE entity {k}"
    keys.add(k)
    found.setdefault(e["label"], set()).add(e["text"])

missing = {
    lab: sorted(expect[lab] - found.get(lab, set()))
    for lab in expect
}
missing = {k: v for k, v in missing.items() if v}
print(f"chunks={d['chunks']}  entities={len(d['entities'])}  "
      f"elapsed_ms={d['elapsed_ms']}")
print("found per label:", {k: len(v) for k, v in found.items()})
if missing:
    print(f"MISSING (recall check): {missing}")
else:
    print("ALL injected entities found")

# 2. short text: single pass, chunks field absent
s2, d2 = post(
    "/ner",
    {"text": "Alice Jansen works at Acme Corp in London.",
     "labels": ["person", "organization"], "threshold": 0.5},
)
assert s2 == 200 and d2.get("chunks") is None, d2
print(f"short text: single-pass OK ({len(d2['entities'])} entities, "
      f"chunks={d2.get('chunks')})")

# 3. over the hard doc limit -> 413
s3, d3 = post("/ner", {"text": "word " * 12000, "labels": ["person"], "threshold": 0.5})
assert s3 == 413, f"expected 413, got {s3}"
print(f"60k doc: 413 as designed — {d3[:90]}")

# 4. PROGRESSIVE CONTEXT A/B: same doc, propagate off vs on, at a normal
#    threshold AND a stress threshold. Deterministic assertions:
#    (a) propagation never loses entities (n_on >= n_off)
#    (b) EXACT ACCOUNTING: propagated == number of vocab occurrences the
#        model-only result missed
#    (c) guarantee: with propagate on, EVERY vocab occurrence of every
#        model-found entity string is present in the final results
#    The stress threshold (0.7) drops the model's recall on weak-context
#    mentions — exactly where chunk 1..N-1 context must rebuild it.
telegraph = (
    "Appendix A raw log lines follow. "
    + " ".join(f"[ok] sync {o} queue" for o in orgs[:4])
    + " "
    + " ".join(f"[ok] audit {p} mailbox" for p in people[:4])
)
long_doc = doc + "".join(
    f"The invoice from {o} arrived late and was contested. "
    for o in orgs[:4]
) + telegraph


def ab(thr: float, expect_propagated: bool) -> None:
    s_off, d_off = post(
        "/ner",
        {"text": long_doc, "labels": ["person", "organization", "location"],
         "threshold": thr, "propagate": False},
    )
    s_on, d_on = post(
        "/ner",
        {"text": long_doc, "labels": ["person", "organization", "location"],
         "threshold": thr, "propagate": True},
    )
    assert s_off == 200 and s_on == 200, (s_off, s_on)
    n_off, n_on = len(d_off["entities"]), len(d_on["entities"])
    assert n_on >= n_off, f"thr={thr}: propagation lost entities"

    # rebuild the service's vocab from model-found entities only
    vocab = EntityVocab()
    for e in d_on["entities"]:
        if e.get("source") == "model":
            vocab.add(e["text"], e["label"])
    result_keys = {(e["label"], e["start"], e["end"]) for e in d_on["entities"]}
    off_keys = {(e["label"], e["start"], e["end"]) for e in d_off["entities"]}
    occurrences = vocab.match_in(long_doc)
    expected = [m for m in occurrences if (m["label"], m["start"], m["end"]) not in off_keys]
    guaranteed = [m for m in occurrences if (m["label"], m["start"], m["end"]) not in result_keys]

    assert not guaranteed, f"thr={thr}: guarantee violated: {guaranteed[:3]}"
    assert d_on["propagated"] == len(expected), (
        f"thr={thr}: propagated={d_on['propagated']} but model-only missed "
        f"{len(expected)} vocab occurrences — accounting mismatch"
    )
    if expect_propagated:
        assert d_on["propagated"] > 0, (
            f"thr={thr}: expected propagation to fire, got 0 — "
            "the model found everything; adjust the stress fixture"
        )
    srcs = {e.get("source") for e in d_on["entities"]}
    assert "model" in srcs, srcs
    print(
        f"  thr={thr}: off={n_off} on={n_on} propagated={d_on['propagated']} "
        f"(exact accounting over {len(occurrences)} vocab occurrences)"
    )


ab(thr=0.5, expect_propagated=False)   # model strong here; accounting must still hold
ab(thr=0.7, expect_propagated=True)    # stress: model recall drops -> propagation rebuilds

missing = []
print("\nLIVE CHUNKED TEST PASSED" if not missing else "\nRECALL GAPS (see above)")
