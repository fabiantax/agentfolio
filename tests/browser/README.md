# agentfolio browser-E2E journey

The only gate that runs the SHIPPED bundle in the SHIPPING engine (headless
Chromium). Journey: unlock gate -> real .docx upload via the widget ->
extraction settles -> question -> assert streamed answer carries the sentinel
fact + rendered citation -> assert ZERO window page errors.

## Run locally

    pip install playwright argon2-cffi
    playwright install chromium
    python3 tests/browser/run_journey.py

Prints JOURNEY-PASS / JOURNEY-FAIL: <reason>. Binds 127.0.0.1:8293 (API under
test) and :8191 (deterministic chat stub, NIM pointed at it). Fresh tmpdir
sqlite per run; no S3 (in-memory store, ASPNETCORE_ENVIRONMENT=Development).

## CI

`browser-e2e` job in .gitea/workflows/ci.yml (PR-only). No `container:` key
(host-mode lane), no `playwright install-deps` (apt-get fallback 127 on the
Fedora runner — host deps suffice). Full recipe + flake classes:
skill `browser-e2e-agentfolio` and tests/browser/run_journey.py docstring.
