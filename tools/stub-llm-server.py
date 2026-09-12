#!/usr/bin/env python3
"""
Stub LLM server for developing ButterKnife without real models.

Speaks enough of each protocol for the app to list models, stream a canned markdown reply
(one token at a time, with usage/timing fields), and transcribe audio:

  Ollama            GET /api/tags, /api/ps, POST /api/show, /api/chat (NDJSON)
  OpenAI-compatible GET /v1/models, POST /v1/chat/completions (SSE), LM Studio's /api/v0/models/{id}
  Anthropic         GET /v1/models (when x-api-key is present), POST /v1/messages (SSE)
  whisper.cpp       POST /inference (returns canned text); /v1/audio/transcriptions answers 404 like whisper.cpp

Run it, then add connections in the app pointing at http://127.0.0.1:<port> (Ollama / whisper.cpp)
or http://127.0.0.1:<port>/v1 (OpenAI-compatible / Anthropic). Each request is logged with the roles
and image counts it carried, which is how the wire format is checked during manual testing.

    python3 tools/stub-llm-server.py [--port 11434] [--delay 0.08]
"""
import argparse, json, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REPLY = """## Markdown check

Here is **bold**, *italic*, and `inline code`. Raw html: <b>should be escaped</b>

```python
def hi():
    return "hello"
```

1. first
2. second

| col a | col b |
|-------|-------|
| 1     | 2     |

> a quote
"""
import re
DELAY = 0.08
WORDS = re.findall(r"\S+\s*|\s+", REPLY)

class H(BaseHTTPRequestHandler):
    def _json(self, obj):
        b = json.dumps(obj).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)

    def do_GET(self):
        if self.path == "/api/ps":
            self._json({"models": [{"name": "fake-llama:8b", "model": "fake-llama:8b", "context_length": 8192}]})
        elif self.path.startswith("/api/v0/models/"):
            mid = self.path.rsplit("/", 1)[-1]
            self._json({"id": mid, "type": "vlm" if mid == "fake-gpt-vision" else "llm", "max_context_length": 4096})
        elif self.path == "/api/tags":
            self._json({"models": [{"name": "fake-llama:8b"}, {"name": "fake-tiny:1b"}]})
        elif self.path.startswith("/v1/models"):
            if self.headers.get("x-api-key"):
                self._json({"data": [{"id": "fake-claude", "display_name": "Fake Claude", "created_at": "2026-01-01T00:00:00Z", "type": "model"}], "has_more": False, "first_id": "fake-claude", "last_id": "fake-claude"})
            else:
                self._json({"object": "list", "data": [{"id": "fake-gpt", "object": "model"}, {"id": "fake-gpt-vision", "object": "model"}]})
        else:
            self.send_response(404); self.end_headers()

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0)); raw = self.rfile.read(n)
        if self.path == "/v1/audio/transcriptions":
            print("POST /v1/audio/transcriptions bytes=%d has_file=%s has_model=%s" % (n, b'name="file"' in raw, b'name="model"' in raw), flush=True)
            b = b"File Not Found (/v1/audio/transcriptions)"
            self.send_response(404); self.send_header("Content-Type", "text/plain"); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b); return
        if self.path == "/inference":
            wav = raw.find(b"RIFF") >= 0 and raw.find(b"WAVE") >= 0
            print("POST /inference bytes=%d has_file=%s wav=%s" % (n, b'name="file"' in raw, wav), flush=True)
            self._json({"text": "Transcribed by the whisper.cpp-style stub."}); return
        body = json.loads(raw or b"{}")
        def imgs(m):
            c = m.get("content")
            if isinstance(c, list):
                return sum(1 for part in c if part.get("type") in ("image_url", "image"))
            return len(m.get("images") or [])
        print("POST", self.path, body.get("model"), [(m.get("role"), imgs(m)) for m in body.get("messages", [])], flush=True)
        if self.path == "/api/show":
            caps = ["completion", "vision"] if body.get("model") == "fake-llama:8b" else ["completion"]
            self._json({"capabilities": caps, "model_info": {"general.architecture": "llama", "llama.context_length": 131072}}); return
        if self.path == "/api/chat":
            self.send_response(200); self.send_header("Content-Type", "application/x-ndjson"); self.end_headers()
            for w in WORDS:
                self.wfile.write((json.dumps({"model": body["model"], "message": {"role": "assistant", "content": w}, "done": False}) + "\n").encode()); self.wfile.flush(); time.sleep(DELAY)
            prompt_chars = sum(len(m.get("content") or "") for m in body.get("messages", []))
            self.wfile.write((json.dumps({"model": body["model"], "message": {"role": "assistant", "content": ""}, "done": True, "prompt_eval_count": max(1, prompt_chars // 4), "eval_count": len(WORDS), "prompt_eval_duration": 180000000, "eval_duration": int(len(WORDS) * 0.08 * 1e9)}) + "\n").encode()); self.wfile.flush()
        elif self.path == "/v1/chat/completions":
            self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.end_headers()
            for w in WORDS:
                self.wfile.write(("data: " + json.dumps({"choices": [{"delta": {"content": w}, "index": 0}]}) + "\n\n").encode()); self.wfile.flush(); time.sleep(DELAY)
            prompt_chars = sum(len(m.get("content") if isinstance(m.get("content"), str) else "") for m in body.get("messages", []))
            self.wfile.write(("data: " + json.dumps({"choices": [], "usage": {"prompt_tokens": max(1, prompt_chars // 4), "completion_tokens": len(WORDS)}}) + "\n\n").encode()); self.wfile.flush()
            self.wfile.write(b"data: [DONE]\n\n"); self.wfile.flush()
        elif self.path == "/v1/messages":
            print("  anthropic system:", repr(body.get("system")), "key:", self.headers.get("x-api-key"), flush=True)
            self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.end_headers()
            def ev(name, obj):
                self.wfile.write(("event: %s\ndata: %s\n\n" % (name, json.dumps(obj))).encode()); self.wfile.flush()
            ev("message_start", {"type": "message_start", "message": {"id": "msg_1", "type": "message", "role": "assistant", "model": body["model"], "content": [], "stop_reason": None, "stop_sequence": None, "usage": {"input_tokens": 1, "output_tokens": 1}}})
            ev("content_block_start", {"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}})
            for w in WORDS:
                ev("content_block_delta", {"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": w}}); time.sleep(DELAY)
            ev("content_block_stop", {"type": "content_block_stop", "index": 0})
            ev("message_delta", {"type": "message_delta", "delta": {"stop_reason": "end_turn", "stop_sequence": None}, "usage": {"output_tokens": 5}})
            ev("message_stop", {"type": "message_stop"})
        else:
            self.send_response(404); self.end_headers()

if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="Stub LLM server for ButterKnife development")
    ap.add_argument("--port", type=int, default=11434)
    ap.add_argument("--delay", type=float, default=0.08, help="seconds between streamed tokens")
    args = ap.parse_args()
    DELAY = args.delay
    print("stub LLM server on http://127.0.0.1:%d (token delay %.2fs)" % (args.port, DELAY), flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), H).serve_forever()
