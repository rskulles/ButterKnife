#!/usr/bin/env python3
"""
Simulate a browser's first load of ButterKnife: GET /, fetch every asset the page references in parallel (with the
headers browsers send, including Range for fonts/images), then negotiate and open the Blazor SignalR websocket, and
exit. Used to reproduce the macOS hardened-runtime crash (see CLAUDE.md "Releases and the bundled build"): start a
published build with BUTTERKNIFE_NO_BROWSER=1, run three of these concurrently, check the server log for "Fatal error".

    python3 tools/browser-sim.py [http://localhost:5175]
"""
import base64, json, os, re, socket, sys, threading, urllib.request
BASE = sys.argv[1].rstrip("/") if len(sys.argv) > 1 else "http://localhost:5175"
H = {"Accept-Encoding": "gzip, deflate, br", "Accept": "*/*", "User-Agent": "Mozilla/5.0 sim"}
def get(path, headers=None):
    req = urllib.request.Request(BASE + path, headers={**H, **(headers or {})})
    with urllib.request.urlopen(req, timeout=10) as r: return r.status, r.read()
status, html = get("/")
html = html.decode("utf-8", "replace")
assets = set(re.findall(r'(?:href|src)="([^"]+)"', html))
assets = [a for a in assets if not a.startswith(("http", "#", "data:"))]
threads = []
for a in assets + ["lib/bootstrap-icons/font/fonts/bootstrap-icons.woff2", "throbber.png", "apple-touch-icon.png"]:
    hdr = {"Range": "bytes=0-"} if a.endswith((".woff2", ".png")) else {}
    t = threading.Thread(target=lambda a=a, h=hdr: get("/" + a.lstrip("/"), h)); t.start(); threads.append(t)
for t in threads: t.join()
# SignalR: negotiate then websocket upgrade, send the blazorpack handshake, read a frame, close.
req = urllib.request.Request(BASE + "/_blazor/negotiate?negotiateVersion=1", data=b"", headers=H)
with urllib.request.urlopen(req, timeout=10) as r: token = json.loads(r.read())["connectionToken"]
host, _, port = BASE.split("//", 1)[1].partition(":")
s = socket.create_connection((host, int(port or 80)), timeout=10)
key = base64.b64encode(os.urandom(16)).decode()
s.sendall((f"GET /_blazor?id={token} HTTP/1.1\r\nHost: {host}:{port or 80}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
           f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nOrigin: {BASE}\r\n\r\n").encode())
resp = s.recv(4096)
assert b"101" in resp.split(b"\r\n")[0], resp[:80]
payload = b'{"protocol":"blazorpack","version":1}\x1e'
mask = os.urandom(4)
frame = bytes([0x82, 0x80 | len(payload)]) + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
s.sendall(frame)
s.settimeout(3)
try: s.recv(4096)
except Exception: pass
s.close()
print(f"sim ok: {len(assets)} assets, websocket handshake done")
