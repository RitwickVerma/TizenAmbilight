#!/usr/bin/env python3
"""Check the Apps2Samsung catalog entry against the config template it patches.

Apps2Samsung's install form fills in `user_settings` by exact find/replace on
res/config.json, which the TPK ships from TizenAmbilight/config.example.json.
If a `find` string drifts from the template, every install fails, so this
check runs in CI:

  * each `find` occurs exactly once in the template
  * the template, patched with sample values, is still valid JSON and holds them

Usage: python3 tools/check-apps2samsung.py   (exit 0 = ok, 1 = problem)
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ENTRY = os.path.join(ROOT, "packaging", "apps2samsung", "RitwickVerma__TizenAmbilight.json")
TEMPLATE = os.path.join(ROOT, "TizenAmbilight", "config.example.json")
SAMPLES = {"text": "192.168.1.50", "number": "60"}


def main():
    entry = json.load(open(ENTRY, encoding="utf-8"))
    template = open(TEMPLATE, encoding="utf-8").read()
    json.loads(template)
    us = entry["user_settings"]
    if us["file"] != "res/config.json":
        return f"user_settings.file is {us['file']!r}, but the TPK ships res/config.json"
    patched = template
    expected = {}
    for f in us["fields"]:
        n = template.count(f["find"])
        if n != 1:
            return f"field {f['key']}: find string occurs {n} times in the template (need exactly 1)"
        value = SAMPLES[f["type"]]
        if f["key"] == "map_reverse":
            value = "1"
        if "pattern" in f and not re.fullmatch(f["pattern"], value):
            return f"field {f['key']}: sample value {value!r} does not match its own pattern"
        patched = patched.replace(f["find"], f["replace"].replace("{value}", value))
        key = re.match(r'\s*"(\w+)"', f["find"]).group(1)
        expected[key] = value
    try:
        got = json.loads(patched)
    except ValueError as e:
        return f"template is not valid JSON after patching: {e}"
    for key, value in expected.items():
        if str(got.get(key)) != value:
            return f"after patching, {key} = {got.get(key)!r}, expected {value!r}"
    print(f"ok: {len(us['fields'])} user_settings fields patch {us['file']} cleanly")
    return None


if __name__ == "__main__":
    err = main()
    if err:
        print("FAIL: " + err, file=sys.stderr)
        sys.exit(1)
