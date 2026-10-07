#!/usr/bin/env python3
"""Tiny capture-control endpoint for the TizenAmbilight app.

GET /control -> 200 with the contents of /tmp/amb-control, or "run" when absent.
The app treats a body containing "pause" as: stop making capture calls (stay alive).

Why: `tizen install` SIGKILLs the app mid-capture, which leaves the TV's scaler wedged
(rc=-2 on every later call) until reboot. 3/3 deploys did this. Pausing first means the
app holds no capture in flight when it is killed.

SECURITY: **read-only and UNAUTHENTICATED by design.** It only ever serves a file that
must be written locally — there is no POST/PUT handler, so a remote host can read the
control body but cannot change it. Keep it on a trusted network anyway (the body is
plain text and includes the tuning state), and prefer --bind to a single interface.
See SECURITY.md.

Usage:
  echo pause > /tmp/amb-control     # app stops calling capture
  rm -f /tmp/amb-control            # app resumes (needs a restart if it already paused)

  python3 control-server.py [--bind 192.168.1.50] [--port 8792]
                            [--file /tmp/amb-control]
"""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ARGS = argparse.ArgumentParser()
ARGS.add_argument("--bind", default="0.0.0.0")
ARGS.add_argument("--port", type=int, default=8792)
ARGS.add_argument("--file", default="/tmp/amb-control")
OPTS = ARGS.parse_args()
CTL = OPTS.file


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    # A socket timeout, so one stalled client cannot hold a worker thread forever.
    timeout = 10

    def _reply(self, code, body):
        self.send_response(code)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except Exception:
            pass

    def do_GET(self):
        try:
            with open(CTL) as f:
                body = f.read().strip() or "run"
        except Exception:
            body = "run"
        self._reply(200, body.encode())

    # Anything other than a read is refused: this endpoint is deliberately not writable.
    def do_POST(self):
        self._reply(405, b"read-only endpoint")

    do_PUT = do_DELETE = do_PATCH = do_POST

    def log_message(self, *a):
        pass


if __name__ == "__main__":
    print(f"control endpoint on {OPTS.bind}:{OPTS.port}  (file {CTL}, read-only)", flush=True)
    ThreadingHTTPServer((OPTS.bind, OPTS.port), H).serve_forever()
