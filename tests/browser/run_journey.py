#!/usr/bin/env python3
"""Browser-E2E journey (fab-swarm af-u1), python-playwright sync API.

Flow:
  (a) build + launch src/AgentFolio.Api on 127.0.0.1:8292 with a fresh tmp
      global.db, admin token, NIM pointed at the deterministic chat stub;
  (b) seed tenant 'gatelane' via admin API + Argon2id share pw hash written
      straight into the global.db tenants.share_pw_hash column;
  (c) start tests/browser/chat_stub.py on 127.0.0.1:8191;
  (d) drive headless Chromium: unlock /share/gatelane with 'lane-pw',
      upload a real (zipfile-built) .docx into the chat widget, send a
      question, assert the streamed answer cites MARBLE-FALCON and [DOC 1],
      assert ZERO pageerror events;
  (e) teardown; prints JOURNEY-PASS or JOURNEY-FAIL: <reason>.

Run:  python3 tests/browser/run_journey.py
Needs: pip install playwright && playwright install chromium
"""
import os
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.request
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
API_PORT = 8293  # 8290-8292 are taken: 8290 staging, 8190 demo, 8292 PRODUCTION deploy
STUB_PORT = 8191
API_URL = f"http://127.0.0.1:{API_PORT}"
STUB_URL = f"http://127.0.0.1:{STUB_PORT}"
ADMIN_TOKEN = "browser-e2e-admin"
SHARE_PW = "lane-pw"
SENTINEL_ANSWER = (
    "The MARBLE-FALCON consortium funded the quantum relay rollout. [DOC 1]"
)
QUESTION = "Which consortium funded the quantum relay rollout?"
DOCX_SENTINEL = (
    "Project OBSIDIAN-NEBULA covers the quantum relay rollout, "
    "funded by the MARBLE-FALCON consortium."
)

# Argon2id parameters MUST match PasswordGate.HashPassword
# (src/AgentFolio.Platform/Auth/PasswordGate.cs: timeCost 3, memoryCost 64 MiB,
# parallelism 1, hybrid addressing, 32-byte hash).


def hash_share_password(password: str) -> str:
    """Replicates PasswordGate.HashPassword: Argon2id t=3 m=65536 p=1, 32B,
    PHC-encoded (Isopoh library emits standard PHC strings)."""
    import base64
    import secrets

    from argon2.low_level import Type, hash_secret_raw

    salt = secrets.token_bytes(16)
    raw = hash_secret_raw(
        secret=password.encode(),
        salt=salt,
        time_cost=3,
        memory_cost=65536,
        parallelism=1,
        hash_len=32,
        type=Type.ID,
    )
    b64 = base64.b64encode(raw).decode().rstrip("=")
    sb64 = base64.b64encode(salt).decode().rstrip("=")
    return f"$argon2id$v=19$m=65536,t=3,p=1${sb64}${b64}"


def wait_for_port(port: int, timeout_s: float, what: str):
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        s = socket.socket()
        s.settimeout(0.5)
        try:
            if s.connect_ex(("127.0.0.1", port)) == 0:
                return
        finally:
            s.close()
        time.sleep(0.3)
    raise RuntimeError(f"{what} did not open port {port} within {timeout_s}s")


def make_docx(path: Path) -> None:
    with zipfile.ZipFile(path, "w") as z:
        z.writestr(
            "[Content_Types].xml",
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
            '<Default Extension="xml" ContentType="application/xml"/>'
            '<Override PartName="/word/document.xml" ContentType='
            '"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
            "</Types>",
        )
        z.writestr(
            "_rels/.rels",
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" '
            'Target="word/document.xml"/></Relationships>',
        )
        z.writestr(
            "word/document.xml",
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
            "<w:body><w:p><w:r><w:t>" + DOCX_SENTINEL + "</w:t></w:r></w:p></w:body></w:document>",
        )


def seed(tmpdir: Path) -> str:
    """Create tenant + set share pw hash. Returns the tenant id."""
    req = urllib.request.Request(
        f"{API_URL}/api/tenants",
        data=b'{"slug":"gatelane"}',
        headers={"Content-Type": "application/json", "X-Admin-Token": ADMIN_TOKEN},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=15) as r:
        body = r.read().decode()
        assert r.status == 201, f"tenant create -> HTTP {r.status}: {body}"
    import json

    tenant_id = json.loads(body)["tenantId"]
    print(f"SEED-OK tenant gatelane -> {tenant_id}")

    db = tmpdir / "global.db"
    # Schema guard: confirm the column exists as expected before writing.
    conn = sqlite3.connect(f"file:{db}?mode=rw", uri=True)
    try:
        cols = [row[1] for row in conn.execute("PRAGMA table_info(tenants)")]
        assert "share_pw_hash" in cols, f"tenants columns: {cols}"
        conn.execute(
            "UPDATE tenants SET share_pw_hash = ? WHERE tenant_id = ?",
            (hash_share_password(SHARE_PW), tenant_id),
        )
        conn.commit()
    finally:
        conn.close()
    print("SEED-OK share_pw_hash written")
    return tenant_id


def main() -> int:
    tmpdir = Path(tempfile.mkdtemp(prefix="af-e2e-"))
    env = os.environ.copy()
    env.update(
        AGENTFOLIO_GLOBAL_DB=str(tmpdir / "global.db"),
        AGENTFOLIO_ADMIN_TOKEN=ADMIN_TOKEN,
        NIM_BASE_URL=f"{STUB_URL}/v1",
        NIM_API_KEY="stub-key",
        NIM_MODEL="stub-model",
        ASPNETCORE_ENVIRONMENT="Development",  # in-memory object store (Test path);
        # --no-launch-profile below keeps launchSettings from injecting a
        # colliding applicationUrl (it binds :5218 next to ASPNETCORE_URLS).
    )
    env.pop("AGENTFOLIO_S3_ENDPOINT", None)
    env.pop("AGENTFOLIO_PASSWORD_HASH", None)

    dotnet = os.environ.get("DOTNET", "dotnet")
    subprocess.run(
        [dotnet, "build", str(REPO / "src/AgentFolio.Api")],
        check=True,
        cwd=REPO,
        capture_output=True,
    )
    print("BUILD-OK")

    # Extraction needs the sandbox extractor (env AGENTFOLIO_SANDBOX_EXTRACTOR);
    # the default path only exists in published apps. Release-build it ONCE
    # (same recipe as tests/AgentFolio.E2E/FullJourneyTests.cs) and point the
    # API at it.
    extractor_project = REPO / "src/AgentFolio.Sandbox/AgentFolio.Sandbox.Extractor/AgentFolio.Sandbox.Extractor.csproj"
    subprocess.run(
        [dotnet, "build", str(extractor_project), "-c", "Release", "-v", "q", "--nologo"],
        check=True, cwd=REPO, capture_output=True,
    )
    extractor_dll = extractor_project.parent / "bin" / "Release" / "net10.0" / "AgentFolio.Sandbox.Extractor.dll"
    assert extractor_dll.exists(), f"extractor missing: {extractor_dll}"
    env["AGENTFOLIO_SANDBOX_EXTRACTOR"] = str(extractor_dll)
    print("EXTRACTOR-OK", extractor_dll.name)

    procs = []
    try:
        api = subprocess.Popen(
            [dotnet, "run", "--no-build", "--no-launch-profile",
             "--project", str(REPO / "src/AgentFolio.Api"),
             # --urls instead of ASPNETCORE_URLS: the extractor child inherits the
             # API's env; a stray ASPNETCORE_URLS in the sandbox child's env makes
             # SOME component attempt a Kestrel bind (measured: extraction 134 with
             # 'address already in use' against the API port).
             "--urls", API_URL],
            cwd=REPO,
            env=env,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
        )
        procs.append(api)
        wait_for_port(API_PORT, 90, "API")
        print(f"API-OK on {API_URL}")

        seed(tmpdir)

        stub = subprocess.Popen(
            [sys.executable, str(REPO / "tests/browser/chat_stub.py")],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        procs.append(stub)
        wait_for_port(STUB_PORT, 15, "chat-stub")
        print(f"STUB-OK on {STUB_URL}")

        from playwright.sync_api import sync_playwright

        docx_path = tmpdir / "obsidian-nebula.docx"
        make_docx(docx_path)

        page_errors: list[str] = []
        with sync_playwright() as p:
            browser = p.chromium.launch(headless=True)
            page = browser.new_page()
            page.on("pageerror", lambda e: page_errors.append(str(e)))

            # 1. Gate: fill password, submit, expect portfolio + widget (200, no redirect)
            page.goto(f"{API_URL}/share/gatelane", wait_until="domcontentloaded")
            page.fill('input[name="password"]', SHARE_PW)
            page.click('button[type="submit"]')
            page.wait_for_selector("agentfolio-chat", timeout=15_000)
            print("UNLOCK-OK")

            # 2. Upload a real .docx via the widget's hidden file input
            file_input = page.locator("agentfolio-chat input[type=file]")
            file_input.set_input_files(str(docx_path))
            page.wait_for_selector("agentfolio-chat .bubble", timeout=30_000)
            # wait for extraction to settle ('doc ready' status)
            try:
                page.wait_for_function(
                    """() => {
                      const w = document.querySelector('agentfolio-chat');
                      const s = w && w.shadowRoot && w.shadowRoot.querySelector('.status');
                      return s && /doc ready|doc: extraction failed/i.test(s.textContent || '');
                    }""",
                    timeout=90_000,
                )
            except Exception:
                status = page.evaluate(
                    """() => {
                      const w = document.querySelector('agentfolio-chat');
                      return w && w.shadowRoot ? w.shadowRoot.querySelector('.status')?.textContent : null;
                    }"""
                )
                raise AssertionError(f"extraction status never settled; last status: {status!r}")
            print("UPLOAD-OK")

            # 3. Ask the question, wait for the streamed answer bubble
            page.fill("agentfolio-chat input[type=text]", QUESTION)
            page.keyboard.press("Enter")
            # answer bubbles appear after the user bubble; wait for a non-user,
            # non-error bubble that contains the sentinel
            page.wait_for_function(
                """(needle) => {
                  const w = document.querySelector('agentfolio-chat');
                  if (!w || !w.shadowRoot) return false;
                  return [...w.shadowRoot.querySelectorAll('.bubble')]
                    .some(b => !b.classList.contains('user')
                            && !b.classList.contains('error')
                            && b.textContent.includes(needle));
                }""",
                arg="MARBLE-FALCON",
                timeout=60_000,
            )
            answer = page.evaluate(
                """() => {
                  const w = document.querySelector('agentfolio-chat');
                  return [...w.shadowRoot.querySelectorAll('.bubble')
                          ].filter(b => !b.classList.contains('user')
                                    && !b.classList.contains('error'))
                         .map(b => b.textContent).join('\\n');
                }"""
            )
            assert "MARBLE-FALCON" in answer, f"answer missing MARBLE-FALCON: {answer!r}"
            # The server strips [DOC n] markers from text and resolves them into
            # a citations block rendered after the answer (agentfolio-chat.ts
            # renderCitations). The citation bubble carries the doc title/url.
            # Diagnostic + contract: capture the af-done completion event detail
            # (citations array as the SERVER resolved it) instead of guessing DOM.
            done_detail = page.evaluate(
                """async () => {
                  const w = document.querySelector('agentfolio-chat');
                  return await new Promise(res => {
                    w.addEventListener('af-done', e => res(e.detail), { once: true });
                    // if it already fired, this never resolves -> race; fall back to DOM below
                    setTimeout(() => res(null), 12000);
                  });
                }"""
            )
            print("AF-DONE-DETAIL:", done_detail)
            citation_text = page.evaluate(
                """() => {
                  const w = document.querySelector('agentfolio-chat');
                  return [...w.shadowRoot.querySelectorAll('.citations a')]
                         .map(a => a.textContent).join(' | ');
                }"""
            )
            print("ANSWER:", answer[:120])
            print("CITATIONS:", citation_text[:160])
            # Server-side view of the SAME browser session: does /api/session/docs
            # (auth + af_sid cookie) see the extracted doc?
            docs = page.evaluate(
                """async () => {
                  const r = await fetch('/api/session/docs', { credentials: 'include' });
                  return { status: r.status, body: await r.text() };
                }"""
            )
            print("SESSION-DOCS:", docs)
            assert (done_detail and done_detail.get("citations")) or citation_text.strip(), (
                f"no citations: af-done={done_detail!r} dom={citation_text!r}")

            assert len(page_errors) == 0, f"page errors: {page_errors}"
            print(f"PAGE-ERRORS: {len(page_errors)}")
            browser.close()

        print("JOURNEY-PASS")
        return 0

    except Exception as exc:  # noqa: BLE001 — the journey IS the gate
        print(f"JOURNEY-FAIL: {exc}")
        api = next((x for x in procs if x.poll() is None and "dotnet" in " ".join(x.args)), None)
        if api is not None and api.stderr:
            try:
                api.terminate()
                _, err = api.communicate(timeout=5)
                tail = (err or b"").decode(errors="replace")[-2000:]
                if tail.strip():
                    print("API-STDERR-TAIL:", tail)
            except Exception:
                pass
        return 1
    finally:
        for proc in procs:
            if proc.poll() is None:
                proc.terminate()
        for proc in procs:
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()


if __name__ == "__main__":
    sys.exit(main())
