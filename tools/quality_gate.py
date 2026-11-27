#!/usr/bin/env python3
"""quality-gate v3: a ratchet. A change may not leave the repo worse than the commit it builds on.

Measured against the merge base of HEAD and --base: the default branch in a branch pipeline, the
previous tip for a push to the default branch. Nothing is stored in the repo, and CI runs the merge
base's copy of this file, so a change cannot edit the rules it is judged by.

  size   A code file over 800 lines may not grow. Untouched files are not checked, so existing big
         files are grandfathered and can only shrink. Renames keep their history; a new file may be as
         big as the biggest file the same change deletes (a rewrite git does not see as a rename).
  catch  Broad exception handlers in the changed files may not increase in total (moving one between
         files is fine). A comment "justified: <reason>" on the handler's line or the line above
         exempts exactly one handler.
  move   Code may not be renamed to a name the gate does not measure, or replaced by a symlink.
  diff   At most 400 added lines (deletions are free, lock files don't count). A commit in the range
         with a trailer "Large-Change: <reason>" allows more, visibly.

  quality_gate.py --base REF     exit 0 ok, 1 violations, 2 cannot evaluate (fails closed)

Code = a known suffix, or an extensionless file with a python/sh shebang. Generated code is skipped
when the repo marks it "linguist-generated" in .gitattributes, read from the merge base so a change
cannot mark its own files. Not caught: hand-written code under a name already marked generated,
aliases (E = Exception; except E), JS regex and template-literal edge cases. Stdlib only, no network. Canonical copy and tests: the daedalus quality-bar skill; repos vendor it unedited.
"""
import argparse
import fnmatch
import re
import subprocess
import sys
from pathlib import PurePosixPath

VERSION = 3
LIMIT = 800
MAX_ADDED = 400
NOT_COUNTED_IN_DIFF = ["*.lock", "*lock.json", "*.lockb"]
JUSTIFIED = re.compile(r"justified:\s*(.*)")
HASH_FAMILY = {".py", ".pyi", ".sh"}
_TRIPLE = r'"""[\s\S]*?"""|\'\'\'[\s\S]*?\'\'\''
_QUOTED = r'"(?:\\.|[^"\\\n])*"|\'(?:\\.|[^\'\\\n])*\''
LEX_HASH = re.compile(rf"(?P<s>{_TRIPLE}|{_QUOTED})|(?P<c>#[^\n]*)")
LEX_C = re.compile(rf'(?P<s>{_TRIPLE}|@"(?:""|[^"])*"|{_QUOTED}|`(?:\\.|[^`\\])*`)|(?P<c>/\*[\s\S]*?\*/|//[^\n]*)')


def _py(m):
    t = m.group(1).strip()
    return not t or re.search(r"\b(Base)?Exception\b", t) is not None


def _cs(m):
    if m.group(1) is None:
        return True
    words = m.group(1).split()
    return bool(words) and re.fullmatch(r"(global::)?(System\.)?Exception", words[0]) is not None


def _kt(m):
    return m.group(1) is None or m.group(1).split(".")[-1] in ("Exception", "Throwable")


def _java(m):
    types = [re.sub(r"^\s*(final\s+)?", "", alt).split() for alt in m.group(1).split("|")]
    return any(t and t[0].split(".")[-1] in ("Exception", "Throwable") for t in types)


def _always(m):
    return True


PY = [(re.compile(r"^[ \t]*except\*?(?!\w)([^:]*):", re.M), _py), (re.compile(r"\bsuppress\s*\(([^)]*)\)"), _py)]
CS = [(re.compile(r"\bcatch\b\s*(?:\(([^)]*)\)|(?=\{))"), _cs)]
KT = [(re.compile(r"\bcatch\s*\(\s*\w+\s*:\s*([\w.]+)|\brunCatching\b"), _kt)]
JAVA = [(re.compile(r"\bcatch\s*\(([^)]*)\)"), _java)]
JS = [(re.compile(r"\bcatch\s*[({]"), _always)]  # a JS catch, or .catch(, takes everything
DART = [(re.compile(r"(?:\bon\s+([\w.<>?]+)\s+)?\bcatch\s*\("), lambda m: m.group(1) in (None, "Exception", "Object")),
        (re.compile(r"\bon\s+(?:Exception|Object)\s*\{"), _always),
        (re.compile(r"\.catchError\s*\("), _always)]
CPP = [(re.compile(r"\bcatch\s*\(\s*(?:\.\.\.|(?:const\s+)?(?:std::)?exception\b)"), _always)]
SWIFT = [(re.compile(r"\bcatch\s*(?:let\s+\w+\s*)?\{|\btry\?"), _always)]
RULES = {**dict.fromkeys((".py", ".pyi"), PY), ".cs": CS, **dict.fromkeys((".kt", ".kts"), KT), ".java": JAVA,
         **dict.fromkeys((".js", ".mjs", ".cjs", ".jsx", ".ts", ".mts", ".cts", ".tsx", ".qml", ".vue", ".svelte"), JS),
         ".dart": DART, **dict.fromkeys((".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".mm"), CPP),
         ".swift": SWIFT}
CODE = set(RULES) | {".rs", ".go", ".sh", ".m"}  # size and diff only: no exceptions to count


class CannotEvaluate(Exception):
    pass


def git(*args):
    p = subprocess.run(["git", *args], capture_output=True)
    if p.returncode != 0:
        raise CannotEvaluate(f"git {' '.join(args)}: {p.stderr.decode(errors='replace').strip()}")
    return p.stdout.decode(errors="surrogateescape")


def blob(rev, path):
    if path is None:
        return None
    p = subprocess.run(["git", "cat-file", "blob", f"{rev}:{path}"], capture_output=True)
    return p.stdout.decode(errors="replace") if p.returncode == 0 else None


def kind(path, text=None):
    """The rule set a file is measured with (a suffix), or None if it is not code."""
    ext = PurePosixPath(path).suffix
    if ext in CODE:
        return ext
    if ext == "" and text and text.startswith("#!"):
        first = text.split("\n", 1)[0]
        if "python" in first:
            return ".py"
        if re.search(r"\b(ba|z|da|k)?sh\b", first):
            return ".sh"
    return None


def count_lines(text):
    return text.count("\n") + (1 if text and not text.endswith("\n") else 0)


def _blank(s):
    return re.sub(r"[^\n]", " ", s)


def split_code(text, ext):
    """(code, comments): the text with strings and comments blanked, and with only comments kept.
    One left-to-right pass, so a // inside a string is not a comment and a catch inside one is not code."""
    code, notes, pos = [], [], 0
    for m in (LEX_HASH if ext in HASH_FAMILY else LEX_C).finditer(text):
        gap, tok = text[pos:m.start()], m.group(0)
        code += [gap, _blank(tok)]
        notes += [_blank(gap), tok if m.group("c") else _blank(tok)]
        pos = m.end()
    return "".join(code) + text[pos:], "".join(notes) + _blank(text[pos:])


def count_catches(path, text, ext=None):
    ext = ext or kind(path, text)
    if ext not in RULES or not text:
        return 0
    code, notes = split_code(text, ext)
    markers = set()
    for i, line in enumerate(notes.split("\n")):
        m = JUSTIFIED.search(line)
        if m and len(m.group(1).strip().rstrip("*/").strip()) >= 8:
            markers.add(i)
    hits = sorted(m.start() for rx, broad in RULES[ext] for m in rx.finditer(code) if broad(m))
    n = 0
    for start in hits:
        i = code.count("\n", 0, start)
        mark = i if i in markers else i - 1 if i - 1 in markers else None
        if mark is None:
            n += 1
        else:
            markers.discard(mark)  # one marker exempts one handler
    return n


def generated(mb, paths, cwd=None):
    """Paths the merge base's .gitattributes marks linguist-generated (git >= 2.40 for --source)."""
    if not paths:
        return set()
    p = subprocess.run(["git", "check-attr", "-z", f"--source={mb}", "linguist-generated", "--stdin"], cwd=cwd,
                       input="\0".join(paths).encode(errors="surrogateescape") + b"\0", capture_output=True)
    if p.returncode != 0:
        raise CannotEvaluate(f"git check-attr: {p.stderr.decode(errors='replace').strip()}")
    f = p.stdout.decode(errors="surrogateescape").split("\0")
    return {f[i] for i in range(0, len(f) - 2, 3) if f[i + 2] in ("set", "true")}


def changes(mb):
    """(status, old path or None, new path or None) for each file changed from mb to HEAD."""
    f, out, i = git("diff", "-z", "-M", "--name-status", mb, "HEAD").split("\0"), [], 0
    while i < len(f) - 1:
        st = f[i][0]
        if st == "R":
            out.append(("R", f[i + 1], f[i + 2]))
            i += 3
        else:
            out.append((st, None if st == "A" else f[i + 1], None if st == "D" else f[i + 1]))
            i += 2
    return out


def added_lines(mb, gen):
    parts, i, total = git("diff", "-z", "-M", "--numstat", mb, "HEAD").split("\0"), 0, 0
    while i < len(parts):
        if not parts[i]:
            i += 1
            continue
        add, _, path = parts[i].split("\t", 2)
        if path == "":  # a rename: old and new path follow as separate fields
            path, i = parts[i + 2], i + 3
        else:
            i += 1
        if add != "-" and path not in gen and not any(fnmatch.fnmatch(path, g) for g in NOT_COUNTED_IN_DIFF):  # "-" = binary
            total += int(add)
    return total


def evaluate(mb):
    ch = changes(mb)
    gen = generated(mb, sorted({p for _, old, new in ch for p in (old, new) if p}))
    rows = []
    for st, old, new in ch:
        ot, nt = blob(mb, old), blob("HEAD", new)
        ok = kind(old, ot) if old and old not in gen else None
        nk = kind(new, nt) if new and new not in gen else None
        rows.append((st, old, new, ot, nt, ok, nk))
    deleted = max([count_lines(ot) for st, _, _, ot, _, ok, _ in rows if st == "D" and ok and ot] or [0])
    bad, notes, before, after, grew = [], [], 0, 0, []
    for st, old, new, ot, nt, ok, nk in rows:
        if ok and st == "T":
            bad.append(f"move   {old}: code replaced by a symlink or other non-file.")
            nk = None
        elif ok and st == "R" and not nk:
            bad.append(f"move   {old} -> {new}: renamed to a name the gate does not measure.")
        if nk and nt is not None:
            n = count_lines(nt)
            was = count_lines(ot) if ok and ot is not None else deleted if st == "A" else 0
            if n > LIMIT and n > was:
                bad.append(f"size   {new}: {n} lines ({f'was {was}' if was > LIMIT else f'limit {LIMIT}'}). "
                           "Split it; big files may only shrink.")
        c_old = count_catches(old, ot, ok) if ok else 0
        c_new = count_catches(new, nt, nk) if nk else 0
        before, after = before + c_old, after + c_new
        if c_new > c_old:
            grew.append(f"{new} {c_old}->{c_new}")
    if after > before:
        bad.append(f"catch  broad handlers {before} -> {after} ({', '.join(grew)}). Catch the specific error, or "
                   "say why in a comment on the line: 'justified: <reason>' (one comment per handler).")
    added = added_lines(mb, gen)
    if added > MAX_ADDED:
        log = git("log", "--format=%(trailers:key=Large-Change,valueonly)", f"{mb}..HEAD")
        reasons = [r.strip() for r in log.splitlines() if len(r.strip()) >= 10]
        if reasons:
            notes.append(f"diff   {added} added lines, allowed by Large-Change: {reasons[0]}")
        else:
            bad.append(f"diff   {added} added lines (max {MAX_ADDED}). Split the change, or add a trailer "
                       "'Large-Change: <why>' to a commit.")
    return bad, notes


def main(argv=None):
    ap = argparse.ArgumentParser(description="Ratchet: fail a change that makes the repo worse.")
    ap.add_argument("--base", required=True, help="default branch (branch pipelines) or previous tip (pushes)")
    a = ap.parse_args(argv)
    try:
        mb = git("merge-base", a.base, "HEAD").strip()
        bad, notes = evaluate(mb)
    except CannotEvaluate as e:
        print(f"quality-gate v{VERSION}: cannot evaluate, failing closed. {e}")
        return 2
    for line in notes:
        print("note", line)
    for line in bad:
        print("FAIL", line)
    print(f"quality-gate v{VERSION}: {'FAILED ' + str(len(bad)) if bad else 'ok'} (against {mb[:12]})")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
