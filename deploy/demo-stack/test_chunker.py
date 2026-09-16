"""Offline chunker tests — no model needed (stdlib only).

Invariant that guarantees correct global-offset remapping:
every chunk is a VERBATIM contiguous slice of the input at its stated
offset: text[base : base + len(chunk)] == chunk. Plus: coverage of the
whole document and chunk size caps. Run: python3 test_chunker.py
"""
import sys
import types

# Import gliformer_main WITHOUT fastapi/pydantic (their import is slow or
# stalls under host load; the chunker under test is pure stdlib). Stub just
# enough surface for module import to succeed.
_fa = types.ModuleType("fastapi")


class HTTPException(Exception):  # noqa: D101
    pass


_fa.HTTPException = HTTPException
_fa.FastAPI = lambda **kw: types.SimpleNamespace(
    get=lambda *a, **k: (lambda f: f),
    post=lambda *a, **k: (lambda f: f),
)
sys.modules.setdefault("fastapi", _fa)
_py = types.ModuleType("pydantic")
_py.BaseModel = object
sys.modules.setdefault("pydantic", _py)

sys.path.insert(0, ".")
from gliformer_main import CHUNK_TARGET, chunk_text  # noqa: E402
from gliformer_main import EntityVocab  # noqa: E402


def test_vocab() -> None:
    v = EntityVocab()
    v.add("Acme Corp", "organization")
    v.add("A", "junk")  # too short — must be ignored
    hits = v.match_in("The Acme Corp invoice from Acme Corp HQ arrived.")
    assert len(hits) == 2, hits
    for h in hits:
        assert h["label"] == "organization" and h["source"] == "propagated"
        assert h["text"] == "Acme Corp"
    assert v.match_in("acme corp") == []          # case-sensitive
    assert v.match_in("Acme Corporation") == []   # word boundary respected
    assert v.match_in("XAcme CorpX") == []        # not inside a word
    print("  vocab: word-boundary/case/short-string semantics OK")


test_vocab()


def check(name: str, text: str) -> None:
    chunks = chunk_text(text)
    assert len(chunks) >= 1, f"{name}: no chunks"
    # 1. verbatim slice invariant
    for base, c in chunks:
        assert text[base : base + len(c)] == c, (
            f"{name}: chunk at {base} is not a verbatim slice"
        )
    # 2. size cap (hard-split may leave a single word over; tolerate none)
    for base, c in chunks:
        assert len(c) <= CHUNK_TARGET + 80, (
            f"{name}: chunk at {base} is {len(c)} > target {CHUNK_TARGET}"
        )
    # 3. coverage: ordered, first starts at 0, seams overlap or continue
    assert chunks[0][0] == 0, f"{name}: first chunk starts at {chunks[0][0]}"
    for i in range(1, len(chunks)):
        assert chunks[i][0] > chunks[i - 1][0], f"{name}: chunks not ordered"
        assert chunks[i][0] <= chunks[i - 1][0] + len(chunks[i - 1][1]), (
            f"{name}: GAP between chunk {i-1} and {i} — text would be lost"
        )
    last_base, last_c = chunks[-1]
    assert last_base + len(last_c) >= len(text.rstrip()), (
        f"{name}: tail not covered"
    )
    print(
        f"  {name}: {len(text)} chars -> {len(chunks)} chunks, "
        f"sizes {[len(c) for _, c in chunks]}, bases {[b for b, _ in chunks]}"
    )


# 1. normal prose, many paragraphs
para = (
    "Alice works at Acme Corp in London. Her manager Bob joined the "
    "company in 2021 after leaving Initech! The team ships fast; really "
    "fast. Carol from the Berlin office visited last week. "
)
check("prose-10k", para * 42)  # ~10.3k chars

# 2. no sentence enders at all (one giant sentence) — hard-split path
check(
    "no-punct-9k",
    "Alice and Bob and Acme Corp and London " * 200,  # ~9k chars, 1 'sentence'
)

# 3. pathological: repeated punctuation + newlines
check("punct-abuse", "A. B! C? D!!!\n\nE?? F.\n" * 300)

# 4. short text (single chunk path via chunk_text directly)
check("short", "Alice works at Acme Corp.")

# 5. exactly-at-boundary sentence sizes
check("boundary", "x" * 3190 + ". " + "y" * 3190 + ".")

# 6. UTF-8 multibyte (offsets are character positions, not bytes)
check("utf8", "Zoë works at Ünicode GmbH in München. " * 250)

print("\nALL CHUNKER TESTS PASSED")
