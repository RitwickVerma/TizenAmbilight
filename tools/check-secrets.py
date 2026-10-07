#!/usr/bin/env python3
"""Fail a commit (or a CI run) that carries a credential.

Two modes:

  as a pre-commit hook   python3 tools/check-secrets.py --staged
  over a whole tree      python3 tools/check-secrets.py --all

Exit code 0 = clean, 1 = findings (each printed with file, line and a redacted
match). Designed for GitLab's free tier, where server-side secret push
protection is not available.

Scope note: this is a seatbelt, not a guarantee. It catches known shapes and
any `key = "long-literal"` assignment. It cannot catch a novel format, and it
deliberately does not flag short or high-entropy-free strings.
"""
import argparse
import os
import re
import subprocess
import sys

PATTERNS = [
    ("gitlab token", re.compile(rb"glpat-[A-Za-z0-9_\-]{20}")),
    ("gitlab runner token", re.compile(rb"glrt-[A-Za-z0-9_\-]{20}")),
    ("github token", re.compile(rb"gh[pousr]_[A-Za-z0-9]{36}")),
    ("aws access key", re.compile(rb"(AKIA|ASIA)[0-9A-Z]{16}")),
    ("private key block", re.compile(rb"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    ("openai-style key", re.compile(rb"sk-[A-Za-z0-9]{20,}")),
    ("slack/discord webhook", re.compile(rb"https://(hooks\.slack\.com|discord(app)?\.com/api/webhooks)/\S+")),
    ("google api key", re.compile(rb"AIza[0-9A-Za-z_\-]{35}")),
    ("jwt", re.compile(rb"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")),
    ("assignment with literal", re.compile(
        rb"(?i)\b(pass(word|wd|phrase)?|secret|api[_-]?key|access[_-]?token|auth[_-]?token|private[_-]?key)"
        rb"\s*[:=]\s*[\"'][^\"'\s]{8,}[\"']")),
]

# Paths that are allowed to contain pattern text (this scanner's own pattern list,
# test fixtures that assert a scanner rejects something, etc).
ALLOWLIST_PATHS = (
    "tools/check-secrets.py",
    "decoder-selftest/testdata/",
)
ALLOWLIST_MARKERS = (b"allowlist-secret", b"EXAMPLE_NOT_A_SECRET", b"placeholder")

BINARY_EXT = (".png", ".jpg", ".jpeg", ".rgba", ".bin", ".tpk", ".p12", ".pem", ".zip", ".pdf")


def scan_bytes(data, label, out):
    for name, rx in PATTERNS:
        for m in rx.finditer(data):
            hit = m.group(0)
            if any(marker in hit for marker in ALLOWLIST_MARKERS):
                continue
            line = data[:m.start()].count(b"\n") + 1
            safe = hit[:24].decode("utf-8", "replace")
            out.append(f"{label}:{line}: {name}: {safe}…")


def scanned_paths(all_files):
    for p in all_files:
        if p.endswith(BINARY_EXT) or "/.git/" in f"/{p}":
            continue
        if any(p.startswith(a) for a in ALLOWLIST_PATHS):
            continue
        yield p


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--all", action="store_true", help="scan every tracked file")
    ap.add_argument("--staged", action="store_true", help="scan staged content (pre-commit)")
    args = ap.parse_args()
    if not (args.all or args.staged):
        args.staged = True

    findings = []

    if args.all:
        files = subprocess.run(["git", "ls-files"], capture_output=True, text=True).stdout.split()
        for p in scanned_paths(files):
            try:
                with open(p, "rb") as f:
                    scan_bytes(f.read(), p, findings)
            except OSError:
                pass
        print(f"scanned {len(files)} tracked files")

    if args.staged:
        files = subprocess.run(["git", "diff", "--cached", "--name-only", "-z"],
                               capture_output=True).stdout.split(b"\0")
        files = [f.decode() for f in files if f]
        for p in scanned_paths(files):
            blob = subprocess.run(["git", "show", f":{p}"], capture_output=True).stdout
            scan_bytes(blob, p, findings)
        print(f"scanned {len(files)} staged paths")

    if findings:
        print("\nBLOCKED — credential-shaped content found:")
        for f in sorted(set(findings)):
            print("  " + f)
        print("\nIf this is a false positive, add an `allowlist-secret` marker on that line,")
        print("or add the path to ALLOWLIST_PATHS in tools/check-secrets.py.")
        return 1

    print("clean — no credential-shaped content")
    return 0


if __name__ == "__main__":
    sys.exit(main())
