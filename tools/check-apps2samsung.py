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
EDGE_KEYS = {"edge_top": "edgeTop", "edge_right": "edgeRight",
             "edge_bottom": "edgeBottom", "edge_left": "edgeLeft"}


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
        if f["key"] == "map_reverse" or f["key"] in EDGE_KEYS:
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
    edge_fields = {f["key"]: f for f in us["fields"] if f["key"] in EDGE_KEYS}
    if set(edge_fields) != set(EDGE_KEYS):
        return "the install form must expose all four screen edges"
    defaults = json.loads(template)
    for key, config_key in EDGE_KEYS.items():
        if defaults.get(config_key) is not True:
            return f"{config_key} must default to true when an optional form field is left blank"
        field = edge_fields[key]
        if field["type"] != "number" or field.get("required", True):
            return f"field {key}: expected an optional number field"
        for bad in ("2", "-1", "true", "false", "1.5"):
            if re.fullmatch(field["pattern"], bad):
                return f"field {key}: pattern accepts invalid edge value {bad!r}"

    # Apps2Samsung supports number/text fields, so 0/1 is written as a JSON number.
    # ConfigFile reads numbers and booleans identically. Omitted optional fields leave true.
    for mask in range(16):
        edge_patched = template
        expected_edges = {}
        for bit, (key, config_key) in enumerate(EDGE_KEYS.items()):
            value = str((mask >> bit) & 1)
            field = edge_fields[key]
            if not re.fullmatch(field["pattern"], value):
                return f"field {key}: pattern rejects valid value {value!r}"
            edge_patched = edge_patched.replace(field["find"], field["replace"].replace("{value}", value))
            expected_edges[config_key] = int(value)
        edge_got = json.loads(edge_patched)
        for config_key, value in expected_edges.items():
            if edge_got.get(config_key) != value:
                return f"edge mask {mask}: {config_key} was not patched to {value}"

    print(f"ok: {len(us['fields'])} user_settings fields patch {us['file']} cleanly; all 16 edge combinations checked")
    return None


if __name__ == "__main__":
    err = main()
    if err:
        print("FAIL: " + err, file=sys.stderr)
        sys.exit(1)
