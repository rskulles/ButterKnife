#!/usr/bin/env python3
"""
Stub LLM server for developing ButterKnife without real models.

Speaks enough of each protocol for the app to list models, stream a canned markdown reply
(one token at a time, with usage/timing fields), and transcribe audio:

  Ollama            GET /api/tags, /api/ps, POST /api/show, /api/chat (NDJSON)
  OpenAI-compatible GET /v1/models, POST /v1/chat/completions (SSE), LM Studio's /api/v0/models/{id}
  Anthropic         GET /v1/models (when x-api-key is present), POST /v1/messages (SSE)
  whisper.cpp       POST /inference (returns canned text); /v1/audio/transcriptions answers 404 like whisper.cpp

When a chat request offers tools (the Code tab), the reply walks through a fixed scenario instead, one tool call per
request in each protocol's own streaming shape: list_files, read_file README.md, write_file stub-notes.md, edit_file
on it, run_command "ls -la", then a closing summary. A prompt containing "[quick]" skips straight to the summary;
one containing "[skill]" starts with read_skill explore-project; one containing "[look]" reads screenshot.png (an image, so the
picture comes back to the model); one containing "[bg]" instead starts a background
command, reads its output with command_output, then cds into src and runs pwd twice to show the directory sticking.
fake-llama:8b, fake-gpt and fake-gpt-vision report tool support; fake-tiny:1b does not. fake-llama:8b and
fake-gpt-vision can see images; the request log shows (role, image count) per message, so a picture from a tool shows
up on the user message that follows the tool results for Ollama and OpenAI-compatible requests.

Run it, then add connections in the app pointing at http://127.0.0.1:<port> (Ollama / whisper.cpp)
or http://127.0.0.1:<port>/v1 (OpenAI-compatible / Anthropic). Each request is logged with the roles
and image counts it carried, which is how the wire format is checked during manual testing.

    python3 tools/stub-llm-server.py [--port 11434] [--delay 0.08]
"""
import argparse, base64, json, struct, time, zlib
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

Inline math $E = mc^2$ and a display equation:

$$
\\int_0^1 x^2\\,dx = \\frac{1}{3}
$$

```mermaid
graph LR
  A[Prompt] --> B(Model) --> C{Reply}
```
"""
import re

def fake_png(width, height, seed):
    """A gradient PNG written by hand (no Pillow needed): the hue comes from the seed so different seeds look different."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    r0, g0, b0 = (seed * 37) % 256, (seed * 91) % 256, (seed * 53) % 256
    rows = b""
    for y in range(height):
        t = y / max(1, height - 1)
        rows += b"\x00" + bytes(v for x in range(width) for v in (int(r0 * (1 - t) + 255 * t * x / width), int(g0 * (1 - t) + 80 * t), int(b0 * (1 - t) + 255 * t)))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
DELAY = 0.08
WORDS = re.findall(r"\S+\s*|\s+", REPLY)
CONTINUATION = re.findall(r"\S+\s*|\s+", "and here is the rest of the reply, continued from where it stopped.")
TITLE = re.findall(r"\S+\s*|\s+", "\"Stub Title From The Model.\"")
CODE_SUMMARY = re.findall(r"\S+\s*|\s+", "- The user asked the stub agent to look around the project.\n- It listed the folder, read `README.md`, wrote and edited `stub-notes.md` and ran `ls -la` (which passed).\n")
TOOL_STEPS = [
    ("I'll start by looking at what is in the project folder.", "list_files", {}),
    ("Let me read the README.", "read_file", {"path": "README.md"}),
    ("Now I'll write a notes file.", "write_file", {"path": "stub-notes.md", "content": "# Notes from the stub model\n\nWritten by the stub.\n\nStatus: draft\n"}),
    ("And mark it reviewed.", "edit_file", {"path": "stub-notes.md", "old_string": "Status: draft", "new_string": "Status: reviewed"}),
    ("Finally, let me check the folder from the shell.", "run_command", {"command": "ls -la"}),
]
TOOL_SUMMARY = "Done. I listed the folder, read `README.md`, wrote and edited `stub-notes.md`, and ran `ls -la`.\n\n- one\n- two\n"

def tool_progress(body):
    """The latest real prompt and the tool names already called after it, in any of the three protocols."""
    msgs = body.get("messages", [])
    def text_of(c):
        return c if isinstance(c, str) else " ".join(b.get("text", "") for b in (c or []) if isinstance(b, dict))
    def is_prompt(m):
        c = m.get("content")
        # The Code tab moves pictures returned by tools into a user message after the results; that is not a prompt.
        if text_of(c).startswith("The pictures returned by the tool calls above"):
            return False
        return m.get("role") == "user" and (isinstance(c, str) or not any(isinstance(b, dict) and b.get("type") == "tool_result" for b in (c or [])))
    start = max((i for i, m in enumerate(msgs) if is_prompt(m)), default=-1)
    prompt = msgs[start].get("content") if start >= 0 else ""
    if isinstance(prompt, list):
        prompt = " ".join(b.get("text", "") for b in prompt if isinstance(b, dict))
    called = []
    # After a compaction inside the turn (CodeCompactor.ResentNote) the earlier calls are gone from the request; the
    # stub's own summary in the system prompt lists them.
    if "[Sent again:" in str(prompt):
        system = body.get("system") or next((m.get("content") for m in msgs if m.get("role") == "system"), "")
        if isinstance(system, list):
            system = " ".join(b.get("text", "") for b in system if isinstance(b, dict))
        found = re.findall(r"Tools called so far: ([\w, ]*)\.", system or "")
        if found:
            called += [t.strip() for t in found[-1].split(",") if t.strip()]
    for m in msgs[start + 1:]:
        for c in m.get("tool_calls") or []:
            called.append((c.get("function") or {}).get("name"))
        if isinstance(m.get("content"), list):
            called += [b.get("name") for b in m["content"] if isinstance(b, dict) and b.get("type") == "tool_use"]
    return prompt or "", called

THINKING = re.findall(r"\S+\s*|\s+", "The user wants a markdown check. I should show a heading, some inline styles, a code block, a list, a table and a quote. ")

class H(BaseHTTPRequestHandler):
    def _json(self, obj):
        b = json.dumps(obj).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)

    def do_GET(self):
        if self.path == "/v1/loras":
            self._json({"object": "list", "folder": "/stub/loras", "data": [{"name": "watercolor", "file": "watercolor.safetensors", "size": 1}, {"name": "sketch", "file": "sketch.safetensors", "size": 1}]}); return
        if self.path == "/api/ps":
            self._json({"models": [{"name": "fake-llama:8b", "model": "fake-llama:8b", "context_length": 8192}]})
        elif self.path.startswith("/api/v0/models/"):
            mid = self.path.rsplit("/", 1)[-1]
            self._json({"id": mid, "type": "vlm" if mid == "fake-gpt-vision" else "llm", "max_context_length": 4096, "capabilities": ["tool_use"] if mid in ("fake-gpt", "fake-gpt-vision") else []})
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
        if self.path == "/v1/images/generations":
            # Crayon Cloud / OpenAI images API: a small gradient PNG, after a pause that scales with --delay.
            body = json.loads(raw or b"{}")
            image = body.get("image")
            src = None
            if image:
                # Image to image: any base64 (or data: URL) counts as a source; report a fake shape and the strength back.
                src = {"width": 256, "height": 192, "strength": body.get("strength", 0.6)}
            print("POST /v1/images/generations", repr(body.get("prompt", ""))[:60], body.get("size"), "steps=%s seed=%s loras=%s image=%s strength=%s" % (body.get("steps"), body.get("seed"), body.get("loras"), (len(image) if image else None), body.get("strength")), flush=True)
            time.sleep(DELAY * 20)
            seed = body.get("seed") if body.get("seed") is not None else 4242
            self._json({"created": int(time.time()), "data": [{"b64_json": base64.b64encode(fake_png(256, 256, seed)).decode(), "seed": seed}],
                        "crayoncloud": {"model": "z-image-turbo", "engine": "stub", "width": 256, "height": 256, "steps": body.get("steps") or 8, "seconds": round(DELAY * 20, 1), "source": src}})
            return
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
        tuning = {k: body[k] for k in ("options", "think", "temperature", "max_tokens", "chat_template_kwargs", "thinking") if k in body}
        user = next((m.get("content") for m in reversed(body.get("messages", [])) if m.get("role") == "user"), None)
        if isinstance(user, str) and "<document" in user:
            print("  user carries %d document(s), %d chars" % (user.count("<document"), len(user)), flush=True)
        system = next((m.get("content") for m in body.get("messages", []) if m.get("role") == "system"), None)
        if isinstance(system, str) and system:
            print("  system:", repr(system[-160:]), flush=True)
        print("POST", self.path, body.get("model"), [(m.get("role"), imgs(m)) for m in body.get("messages", [])], tuning or "", flush=True)
        if self.path == "/api/show":
            caps = ["completion", "vision", "tools"] if body.get("model") == "fake-llama:8b" else ["completion"]
            self._json({"capabilities": caps, "model_info": {"general.architecture": "llama", "llama.context_length": 131072}}); return
        if body.get("tools"):
            self.tool_reply(body); return
        # Test hooks for the chat page's Continue: a user message containing "[cut]" gets a reply cut off after a few
        # words with a "length" finish; a request whose last message is the assistant's (a continuation) gets the rest.
        msgs = body.get("messages", [])
        last = msgs[-1] if msgs else {}
        continuing = last.get("role") == "assistant"
        cut = (not continuing) and "[cut]" in str(last.get("content", ""))
        # The chat page asks for a title after the first exchange (ChatTitler.Instruction); answer with a plain title
        # and no thinking so the sidebar can be checked.
        titling = (not continuing) and str(last.get("content", "")).startswith("Write a title")
        # The Code tab compacts a long session by asking for a summary (CodeCompactor.SummarizerSystemPrompt).
        summarising = isinstance(system, str) and system.startswith("You summarise sessions between a user and a coding agent")
        summary = CODE_SUMMARY
        if summarising:
            transcript = str(last.get("content", ""))
            print("  code summary request, %d chars of transcript" % len(transcript), flush=True)
            # Lists the tools the latest request has called, so a compaction inside a turn can carry the scenario on (see
            # tool_progress): those after its "User:" line, or the previous summary's list plus the transcript's calls
            # when the transcript starts inside that request.
            prompt_at = max(transcript.rfind("\nUser: "), 0 if transcript.startswith("User: ") else -1)
            earlier = re.findall(r"Tools called so far: ([\w, ]*)\.", transcript) if prompt_at < 0 else []
            tools = [t.strip() for t in (earlier[-1].split(",") if earlier else []) if t.strip()] + re.findall(r"Assistant called (\w+)", transcript[max(prompt_at, 0):])
            summary = CODE_SUMMARY + re.findall(r"\S+\s*|\s+", "- Tools called so far: %s.\n" % ", ".join(tools))
        # Replies start with the model's name so two models answering side by side can be told apart.
        words = CONTINUATION if continuing else TITLE if titling else summary if summarising else ([f"_{body.get('model')}_ ", "says:\n\n"] + (WORDS[:6] if cut else WORDS))
        finish = "length" if cut else "stop"
        if self.path == "/api/chat":
            self.send_response(200); self.send_header("Content-Type", "application/x-ndjson"); self.end_headers()
            # Thinking models (Ollama 0.9+) stream their reasoning in message.thinking before the answer.
            if not continuing and not titling and not summarising:
                for w in THINKING:
                    self.wfile.write((json.dumps({"model": body["model"], "message": {"role": "assistant", "content": "", "thinking": w}, "done": False}) + "\n").encode()); self.wfile.flush(); time.sleep(DELAY)
            for w in words:
                self.wfile.write((json.dumps({"model": body["model"], "message": {"role": "assistant", "content": w}, "done": False}) + "\n").encode()); self.wfile.flush(); time.sleep(DELAY)
            prompt_chars = sum(len(m.get("content") or "") for m in body.get("messages", []))
            self.wfile.write((json.dumps({"model": body["model"], "message": {"role": "assistant", "content": ""}, "done": True, "done_reason": finish, "prompt_eval_count": max(1, prompt_chars // 4), "eval_count": len(words), "prompt_eval_duration": 180000000, "eval_duration": int(len(words) * 0.08 * 1e9)}) + "\n").encode()); self.wfile.flush()
        elif self.path == "/v1/chat/completions":
            self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.end_headers()
            # fake-gpt thinks inline with <think> tags (as Qwen3 does through llama.cpp/LM Studio); other models use the
            # separated reasoning_content field (DeepSeek, vLLM).
            if continuing or titling or summarising:
                pass
            elif body.get("model") == "fake-gpt":
                for w in ["<think>"] + THINKING + ["</think>"]:
                    self.wfile.write(("data: " + json.dumps({"choices": [{"delta": {"content": w}, "index": 0}]}) + "\n\n").encode()); self.wfile.flush(); time.sleep(DELAY)
            else:
                for w in THINKING:
                    self.wfile.write(("data: " + json.dumps({"choices": [{"delta": {"reasoning_content": w}, "index": 0}]}) + "\n\n").encode()); self.wfile.flush(); time.sleep(DELAY)
            for w in words:
                self.wfile.write(("data: " + json.dumps({"choices": [{"delta": {"content": w}, "index": 0}]}) + "\n\n").encode()); self.wfile.flush(); time.sleep(DELAY)
            self.wfile.write(("data: " + json.dumps({"choices": [{"delta": {}, "index": 0, "finish_reason": finish}]}) + "\n\n").encode()); self.wfile.flush()
            prompt_chars = sum(len(m.get("content") if isinstance(m.get("content"), str) else "") for m in body.get("messages", []))
            self.wfile.write(("data: " + json.dumps({"choices": [], "usage": {"prompt_tokens": max(1, prompt_chars // 4), "completion_tokens": len(words)}}) + "\n\n").encode()); self.wfile.flush()
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

    def tool_reply(self, body):
        prompt, called = tool_progress(body)
        steps = ([("This matches a skill; let me read it.", "read_skill", {"name": "explore-project"})] if "[skill]" in prompt else []) + TOOL_STEPS
        if "[look]" in prompt:
            steps = [("Let me look at the screenshot.", "read_file", {"path": "screenshot.png"})]
        if "[bg]" in prompt:
            steps = [
                ("Let me start the server in the background.", "run_command", {"command": "for i in 1 2 3 4 5 6; do echo \"tick $i\"; sleep 1; done; echo finished", "background": True}),
                ("Let me see what it printed since.", "command_output", {"id": "bg1", "wait_seconds": 3}),
                ("Now move into src and check where I am.", "run_command", {"command": "mkdir -p src && cd src && pwd"}),
                ("The directory should have stuck.", "run_command", {"command": "pwd"}),
            ]
        # One call per request, in order: the number of calls made since the prompt says which step is next.
        step = None if "[quick]" in prompt or len(called) >= len(steps) else steps[len(called)]
        text, call = (step[0], (step[1], step[2])) if step else (TOOL_SUMMARY, None)
        words = [f"_{body.get('model')}_ "] + re.findall(r"\S+\s*|\s+", text) if not called else re.findall(r"\S+\s*|\s+", text)
        print("  tools offered: %s; called so far: %s -> %s" % ([t.get("name") or (t.get("function") or {}).get("name") for t in body["tools"]], called, call[0] if call else "summary"), flush=True)
        model = body.get("model")
        # Prompt size grows with what is sent (about four characters a token), so a long session fills fake-llama:8b's
        # 8k window and the Code tab compacts.
        prompt_tokens = max(1, len(json.dumps(body)) // 4)
        print("  prompt ~%d tokens" % prompt_tokens, flush=True)
        if self.path == "/api/chat":
            self.send_response(200); self.send_header("Content-Type", "application/x-ndjson"); self.end_headers()
            def put(obj):
                self.wfile.write((json.dumps(obj) + "\n").encode()); self.wfile.flush()
            for w in words:
                put({"model": model, "message": {"role": "assistant", "content": w}, "done": False}); time.sleep(DELAY)
            if call:
                put({"model": model, "message": {"role": "assistant", "content": "", "tool_calls": [{"function": {"name": call[0], "arguments": call[1]}}]}, "done": False})
            put({"model": model, "message": {"role": "assistant", "content": ""}, "done": True, "done_reason": "stop", "prompt_eval_count": prompt_tokens, "eval_count": len(words) + 10})
        elif self.path == "/v1/chat/completions":
            self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.end_headers()
            def put(obj):
                self.wfile.write(("data: " + json.dumps(obj) + "\n\n").encode()); self.wfile.flush()
            for w in words:
                put({"choices": [{"delta": {"content": w}, "index": 0}]}); time.sleep(DELAY)
            if call:
                # Arguments arrive in fragments, the way vLLM and llama.cpp stream them.
                args = json.dumps(call[1]); third = max(1, len(args) // 3)
                put({"choices": [{"delta": {"tool_calls": [{"index": 0, "id": "call_stub%d" % len(called), "type": "function", "function": {"name": call[0], "arguments": ""}}]}, "index": 0}]})
                for i in range(0, len(args), third):
                    put({"choices": [{"delta": {"tool_calls": [{"index": 0, "function": {"arguments": args[i:i + third]}}]}, "index": 0}]}); time.sleep(DELAY)
            put({"choices": [{"delta": {}, "index": 0, "finish_reason": "tool_calls" if call else "stop"}]})
            put({"choices": [], "usage": {"prompt_tokens": prompt_tokens, "completion_tokens": len(words) + 10}})
            self.wfile.write(b"data: [DONE]\n\n"); self.wfile.flush()
        elif self.path == "/v1/messages":
            self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.end_headers()
            def ev(name, obj):
                self.wfile.write(("event: %s\ndata: %s\n\n" % (name, json.dumps(obj))).encode()); self.wfile.flush()
            ev("message_start", {"type": "message_start", "message": {"id": "msg_1", "type": "message", "role": "assistant", "model": model, "content": [], "stop_reason": None, "stop_sequence": None, "usage": {"input_tokens": prompt_tokens, "output_tokens": 1}}})
            ev("content_block_start", {"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}})
            for w in words:
                ev("content_block_delta", {"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": w}}); time.sleep(DELAY)
            ev("content_block_stop", {"type": "content_block_stop", "index": 0})
            if call:
                args = json.dumps(call[1]); third = max(1, len(args) // 3)
                ev("content_block_start", {"type": "content_block_start", "index": 1, "content_block": {"type": "tool_use", "id": "toolu_stub%d" % len(called), "name": call[0], "input": {}}})
                for i in range(0, len(args), third):
                    ev("content_block_delta", {"type": "content_block_delta", "index": 1, "delta": {"type": "input_json_delta", "partial_json": args[i:i + third]}})
                ev("content_block_stop", {"type": "content_block_stop", "index": 1})
            ev("message_delta", {"type": "message_delta", "delta": {"stop_reason": "tool_use" if call else "end_turn", "stop_sequence": None}, "usage": {"output_tokens": len(words) + 10}})
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
