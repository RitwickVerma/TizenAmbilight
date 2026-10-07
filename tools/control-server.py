#!/usr/bin/env python3
"""Tiny capture-control endpoint for the TizenAmbilight app.

GET /control -> 200 with the contents of /tmp/amb-control, or "run" when absent.
The app treats a body containing "pause" as: stop making capture calls (stay alive).

Why: `tizen install` SIGKILLs the app mid-capture, which leaves the TV's scaler wedged
(rc=-2 on every later call) until reboot. 3/3 deploys did this. Pausing first means the
app holds no capture in flight when it is killed.

Usage:
  echo pause > /tmp/amb-control     # app stops calling capture
  rm -f /tmp/amb-control            # app resumes (needs a restart if it already paused)
"""
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CTL = "/tmp/amb-control"


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

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
        print("GET %s -> %s" % (self.path, body), flush=True)
        self._reply(200, body.encode())

    def log_message(self, *a):
        pass


if __name__ == "__main__":
    print("control endpoint on 0.0.0.0:8792  (file %s)" % CTL, flush=True)
    ThreadingHTTPServer(("0.0.0.0", 8792), H).serve_forever()
