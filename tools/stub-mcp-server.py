#!/usr/bin/env python3
"""
A tiny MCP server for developing and testing ButterKnife's MCP support, with no dependencies: newline-delimited
JSON-RPC over stdin/stdout, the stdio transport. Tools:

  echo     {text}        returns the text
  add      {a, b}        returns a + b
  picture  {}            returns a small PNG (exercises pictures from tools)
  fail     {}            returns an error result (isError)

Add it in ButterKnife under Settings -> MCP servers as a local server with the command
`python3 /path/to/tools/stub-mcp-server.py`. With --log FILE it writes every message it receives to FILE.
"""
import base64, json, struct, sys, zlib

LOG = None


def png():
    """A 16x16 gradient PNG written by hand."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    rows = b"".join(b"\x00" + bytes(v for x in range(16) for v in (x * 16, y * 16, 200)) for y in range(16))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 16, 16, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


TOOLS = [
    {"name": "echo", "description": "Repeat the given text back.",
     "inputSchema": {"type": "object", "properties": {"text": {"type": "string", "description": "What to repeat"}}, "required": ["text"]}},
    {"name": "add", "description": "Add two numbers.",
     "inputSchema": {"type": "object", "properties": {"a": {"type": "number"}, "b": {"type": "number"}}, "required": ["a", "b"]}},
    {"name": "picture", "description": "Return a small test picture.", "inputSchema": {"type": "object"}},
    {"name": "fail", "description": "Always fails (for testing errors).", "inputSchema": {"type": "object", "properties": {}}},
]


def call(name, args):
    if name == "echo":
        return {"content": [{"type": "text", "text": str(args.get("text", ""))}]}
    if name == "add":
        return {"content": [{"type": "text", "text": str(float(args.get("a", 0)) + float(args.get("b", 0)))}]}
    if name == "picture":
        return {"content": [{"type": "text", "text": "Here is the picture."},
                            {"type": "image", "data": base64.b64encode(png()).decode(), "mimeType": "image/png"}]}
    if name == "fail":
        return {"content": [{"type": "text", "text": "The stub was asked to fail."}], "isError": True}
    return None


def respond(message_id, result=None, error=None):
    reply = {"jsonrpc": "2.0", "id": message_id}
    if error is not None:
        reply["error"] = error
    else:
        reply["result"] = result
    sys.stdout.write(json.dumps(reply) + "\n")
    sys.stdout.flush()


def main():
    global LOG
    if "--log" in sys.argv:
        LOG = open(sys.argv[sys.argv.index("--log") + 1], "a")
    print("stub MCP server ready", file=sys.stderr, flush=True)
    for line in sys.stdin:
        if not line.strip():
            continue
        message = json.loads(line)
        if LOG:
            LOG.write(line)
            LOG.flush()
        method, message_id, params = message.get("method"), message.get("id"), message.get("params") or {}
        if message_id is None:
            continue  # a notification (initialized, cancelled, ...)
        if method == "initialize":
            respond(message_id, {"protocolVersion": params.get("protocolVersion", "2025-06-18"),
                                 "capabilities": {"tools": {}},
                                 "serverInfo": {"name": "stub-mcp", "version": "1.0"}})
        elif method == "ping":
            respond(message_id, {})
        elif method == "tools/list":
            respond(message_id, {"tools": TOOLS})
        elif method == "tools/call":
            result = call(params.get("name"), params.get("arguments") or {})
            if result is None:
                respond(message_id, error={"code": -32602, "message": f"Unknown tool: {params.get('name')}"})
            else:
                respond(message_id, result)
        else:
            respond(message_id, error={"code": -32601, "message": f"Method not found: {method}"})


if __name__ == "__main__":
    main()
