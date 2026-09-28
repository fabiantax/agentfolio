"""Deterministic OpenAI-compatible chat stub for browser-E2E journeys.

Stdlib only. Serves POST /v1/chat/completions as an SSE stream with a fixed,
cited answer. The sentinel facts (MARBLE-FALCON consortium, [DOC 1]) are the
journey assertions — keep them in sync with tests/browser/run_journey.py.
"""
from http.server import BaseHTTPRequestHandler, HTTPServer

CHUNK_1 = "The MARBLE-FALCON consortium funded the quantum relay rollout. "
CHUNK_2 = "[DOC 1]"


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if not self.path.rstrip("/").endswith("/chat/completions"):
            self.send_response(404)
            self.end_headers()
            return
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            self.rfile.read(length)
        import json as _json

        def sse(content: str) -> str:
            return "data: " + _json.dumps(
                {"choices": [{"delta": {"content": content}}]}
            ) + "\n\n"

        body = (sse(CHUNK_1) + sse(CHUNK_2) + "data: [DONE]\n\n").encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        pass  # keep test output clean


def serve(host="127.0.0.1", port=8191):
    httpd = HTTPServer((host, port), Handler)
    print(f"chat-stub listening on http://{host}:{port}", flush=True)
    httpd.serve_forever()


if __name__ == "__main__":
    serve()
