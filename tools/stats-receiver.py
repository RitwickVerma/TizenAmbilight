#!/usr/bin/env python3
"""Stats collector for TizenAmbilight.

The app POSTs a JSON report to ``<probeEndpoint>/probe`` every ~5 s, and, when a
frame dump is requested (control token ``dump=1``), posts the raw Y and chroma
planes to ``<probeEndpoint>/file``.

Run this on any host the TV can reach, point the app's ``probeEndpoint``
preference at it, and you get:

  <out>/latest.json    - the newest stats report, verbatim
  <out>/history.log    - one compact line per report
  <out>/appframe_Y|C   - the last dumped planes (NV12: Y then interleaved UV)

Render a dumped frame with:

  cat <out>/appframe_Y <out>/appframe_C > /tmp/f.nv12
  ffmpeg -f rawvideo -pix_fmt nv12 -s 960x540 -i /tmp/f.nv12 -y /tmp/f.png

SECURITY: this endpoint is UNAUTHENTICATED — the app cannot send credentials, so
there is nothing to check them against. It therefore assumes a trusted network
and applies the limits it can:

  --bind         which interface to listen on (default 0.0.0.0 = every one)
  --max-body     reject oversized bodies with 413 (default 8 MiB)
  --allow-name   only these dump filenames are accepted (default appframe_Y,appframe_C)

Do not expose it to an untrusted network. See SECURITY.md.

Usage:
  python3 stats-receiver.py [--port 8791] [--out ./ambstats]
                            [--bind 192.168.1.50] [--max-body 8388608]
                            [--allow-name appframe_Y,appframe_C]
"""
import argparse
import base64
import json
import os
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ARGS = argparse.ArgumentParser()
ARGS.add_argument("--port", type=int, default=8791)
ARGS.add_argument("--out", default="./ambstats")
ARGS.add_argument("--bind", default="0.0.0.0")
ARGS.add_argument("--max-body", type=int, default=8 * 1024 * 1024)
ARGS.add_argument("--allow-name", default="appframe_Y,appframe_C")
OPTS = ARGS.parse_args()

OUT = os.path.abspath(OPTS.out)
os.makedirs(OUT, exist_ok=True)
LATEST = os.path.join(OUT, "latest.json")
HIST = os.path.join(OUT, "history.log")
ALLOWED_NAMES = {n.strip() for n in OPTS.allow_name.split(",") if n.strip()}

# Keep the newest report only, but log a compact line for every one so a
# restart of this process does not erase the history.
SUMMARY_KEYS = ("phase", "version", "window_fps", "capture_ok", "capture_fail",
                "loop_exceptions", "decode", "adaptive", "tone")


def summarise(j):
    return json.dumps({"t": time.strftime("%Y-%m-%d %H:%M:%S"),
                       **{k: j.get(k) for k in SUMMARY_KEYS}})


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    # A socket timeout, so one stalled client cannot hold a worker thread forever.
    timeout = 10

    def _reply(self, code, body=b"ok"):
        self.send_response(code)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except Exception:
            pass

    def do_GET(self):
        self._reply(200, b"stats-receiver alive")

    def do_POST(self):
        try:
            n = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            n = 0
        if n < 0 or n > OPTS.max_body:
            self._reply(413, f"body too large (limit {OPTS.max_body})".encode())
            return
        raw = self.rfile.read(n) if n else b""
        text = raw.decode("utf-8", "replace")

        if self.path.startswith("/file"):
            try:
                d = json.loads(text)
                name = os.path.basename(d.get("name") or "")
                if name not in ALLOWED_NAMES:
                    self._reply(400, f"filename not allowed: {name!r}".encode())
                    return
                blob = base64.b64decode(d.get("data") or "")
                with open(os.path.join(OUT, name), "wb") as f:
                    f.write(blob)
                with open(HIST, "a") as f:
                    f.write(json.dumps({"t": time.strftime("%Y-%m-%d %H:%M:%S"),
                                        "file": name, "bytes": len(blob)}) + "\n")
                self._reply(200, b"saved")
            except Exception as e:
                self._reply(400, f"bad file payload: {e}".encode())
            return

        try:
            j = json.loads(text)
            pretty = json.dumps(j, indent=1)
        except Exception:
            j, pretty = {}, text
        with open(LATEST, "w") as f:
            f.write(pretty)
        with open(HIST, "a") as f:
            f.write(summarise(j) + "\n")
        self._reply(200, b"ok")

    def log_message(self, *a):
        pass


if __name__ == "__main__":
    print(f"listening on {OPTS.bind}:{OPTS.port}  out={OUT}  "
          f"max_body={OPTS.max_body}  allowed={sorted(ALLOWED_NAMES)}", flush=True)
    ThreadingHTTPServer((OPTS.bind, OPTS.port), Handler).serve_forever()
