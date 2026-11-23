#!/usr/bin/env python3
"""Which of the docs' pictures a change makes stale, so tests/e2e/shots/run.sh retakes only those (see README.md there).

  stale.py plan [--git DIR] [--head REF] [--tree DIR] [--also NAMES | --all] [--since REF] [--format text|shell]
      The stale pictures, each with why, and how to take them: pytest's -k expression (every set-up step, then the
      shots that take them) and their names, for E2E_SHOTS_ONLY. --git: the repo whose history says what changed since
      each picture was taken (default: the tree's own); --head: the commit the tree is (default HEAD); --tree: the tree
      the pictures are taken from (default: this file's), whose files that differ from the head's count as changed too.
      --also a,b: those pictures too, stale or not; --all: every picture. --since REF (the last release): notes on what
      changed since then that may change pictures without making any stale, a set-up step or conftest.py, say.
      --format shell prints SHOTS_HEAD=, SHOTS_ONLY= and SHOTS_K= for sh's eval, and the reasons and notes on stderr.
  stale.py record --head SHA --names a,b [--root DIR]
      Writes in shots/taken.toml that those pictures were taken from SHA (run.sh does, after a run).
  stale.py check [--root DIR]
      The map against the tree: every picture docs/images has and test_shots.py takes is in pictures.toml, every glob
      matches a file, and taken.toml names only pictures. Exit 1 on an error.

A picture is stale when, since the commit it was last taken from (taken.toml):
  - a file it shows changed: one its globs in pictures.toml match, or [all]'s, or [window]'s for a whole-window picture
    (for a file in [only_within], only a change within its section);
  - or the shot that takes it changed in test_shots.py: its function, or a helper, constant or import that uses;
  - or it was never taken, or docs/images doesn't have it.
It needn't look different: run.sh's shots compare each picture with docs/images' and keep it only when it does. A
changed set-up step, whose state many pictures show, makes none stale by itself: --since notes it.
Python 3.11 or later and git; standard library only. test_shots.py is read with ast, never imported.
"""
from __future__ import annotations

import argparse
import ast
import difflib
import re
import shlex
import subprocess
import sys
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import impact  # noqa: E402  - the scenario selector's globs, tree walk and top-level definitions

ROOT = HERE.parent.parent.parent
MAP = "tests/e2e/shots/pictures.toml"
TAKEN = "tests/e2e/shots/taken.toml"
SHOTS = "tests/e2e/shots/test_shots.py"
IMAGES = "docs/images"
NAME = re.compile(r"^[a-z0-9][a-z0-9-]*$")
SHA = re.compile(r"^[0-9a-f]{40}$")

TAKEN_HEADER = """\
# When each of the docs' pictures was last taken: the commit its tree was. shots/run.sh writes it after a run (stale.py
# record): commit it with the pictures. stale.py plan reads it: a picture is stale when what it shows changed after
# that commit (see pictures.toml).
"""


class Refused(Exception):
    """Bad arguments, or a map, tree or repo that can't be read."""


# ---------------------------------------------------------------- the map

@dataclass
class PictureMap:
    all: list
    window: list
    pictures: dict    # picture -> its globs, sets expanded
    window_except: tuple = ()
    only_within: dict = None   # glob -> (from, to): a change to the file counts only within that section of it

    def globs_for(self, name, kind):
        framed = kind == "window" and name not in self.window_except
        return self.all + (self.window if framed else []) + self.pictures.get(name, [])

    def section_for(self, path):
        return next((s for g, s in (self.only_within or {}).items() if impact.glob_regex(g).match(path)), None)

    def every_glob(self):
        """Each glob once, with where it's used first."""
        seen = {}
        for where, globs in [("all", self.all), ("window", self.window), *self.pictures.items(), ("only_within", list(self.only_within or {}))]:
            for g in globs:
                seen.setdefault(g, where)
        return seen


def load_map(text):
    """The map, and its problems (an unknown or self-using set, something that isn't a glob or a picture's name)."""
    try:
        data = tomllib.loads(text)
    except tomllib.TOMLDecodeError as e:
        return PictureMap([], [], {}), [f"pictures.toml doesn't parse: {e}"]
    sets = data.get("sets", {})
    problems = []

    def expand(globs, where, stack=()):
        out = []
        if not isinstance(globs, list):
            problems.append(f"{where}: not a list of globs")
            return out
        for g in globs:
            if not isinstance(g, str) or not g:
                problems.append(f"{where}: {g!r} isn't a glob")
            elif g.startswith("@"):
                s = g[1:]
                if s in stack:
                    problems.append(f"{where}: the set {s} uses itself")
                elif s not in sets:
                    problems.append(f"{where}: there's no set {s}")
                else:
                    out += [x for x in expand(sets[s], f"set {s}", stack + (s,)) if x not in out]
            elif g not in out:
                out.append(g)
        return out

    only_within = {}
    for g, section in data.get("only_within", {}).items():
        try:
            if not isinstance(section, dict) or not isinstance(section.get("from"), str):
                raise TypeError("it takes { from = REGEX } or { from = REGEX, to = REGEX }")
            to = section.get("to")
            only_within[g] = (re.compile(section["from"]), re.compile(to) if to is not None else None)
        except (re.error, TypeError) as e:
            problems.append(f"only_within: {g}: {section!r} ({e})")
    m = PictureMap(expand(data.get("all", []), "all"), expand(data.get("window", []), "window"),
                   {n: expand(g, n) for n, g in data.get("pictures", {}).items()}, tuple(data.get("window_except", [])),
                   only_within)
    problems += [f"{n}: not a picture's name (a-z, 0-9 and -)" for n in m.pictures if not NAME.match(n)]
    problems += [f"window_except: {n} isn't a picture" for n in m.window_except if n not in m.pictures]
    return m, problems


def load_taken(text):
    if text is None:
        return {}
    try:
        data = tomllib.loads(text)
    except tomllib.TOMLDecodeError as e:
        raise Refused(f"taken.toml doesn't parse: {e}") from e
    return dict(data.get("taken", {}))


def render_taken(taken):
    lines = [TAKEN_HEADER, "[taken]"]
    lines += [f'{n} = "{taken[n]}"' for n in sorted(taken)]
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------- the shots

@dataclass
class Shot:
    kind: str     # "window" (the whole window) or "detail" (one section or dialog)
    test: str     # the test that takes it
    param: bool   # one case of a parametrized test


def _shot_kind(call):
    f = call.func
    if isinstance(f, ast.Attribute) and f.attr in ("window", "detail") and isinstance(f.value, ast.Name) and f.value.id == "shooter":
        return f.attr
    return None


def _arg(call, index, keyword):
    for k in call.keywords:
        if k.arg == keyword:
            return k.value
    return call.args[index] if index is not None and index < len(call.args) else None


def _text(node):
    return node.value if isinstance(node, ast.Constant) and isinstance(node.value, str) else None


def _argnames(node):
    if (s := _text(node)) is not None:
        return [x.strip() for x in s.split(",")]
    return [_text(e) for e in getattr(node, "elts", [])]


def shots_of(text):
    """Each picture test_shots.py takes -> Shot, and the problems (a picture two tests take). A picture is taken by
    shooter.window(page, name) or shooter.detail(page, element, name) with its name written there, or through a helper
    whose parameter it is (by_hand(..., "new-app-paste")), or in a parametrized test's cases."""
    try:
        tree = ast.parse(text)
    except SyntaxError as e:
        raise Refused(f"test_shots.py doesn't parse (line {e.lineno}: {e.msg})") from e
    funcs = {n.name: n for n in tree.body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef))}
    found, takers = [], {}   # (function, picture, kind, parametrized); function -> {parameter: kind}
    for name, f in funcs.items():
        params = [a.arg for a in f.args.args]
        for call in (n for n in ast.walk(f) if isinstance(n, ast.Call)):
            if (kind := _shot_kind(call)) is None:
                continue
            arg = _arg(call, 1 if kind == "window" else 2, "name")
            if (picture := _text(arg)) is not None:
                found.append((name, picture, kind, False))
            elif isinstance(arg, ast.Name) and arg.id in params:
                takers.setdefault(name, {})[arg.id] = kind
    for name, f in funcs.items():
        for call in (n for n in ast.walk(f) if isinstance(n, ast.Call)):
            callee = call.func.id if isinstance(call.func, ast.Name) else None
            if callee not in takers:
                continue
            params = [a.arg for a in funcs[callee].args.args]
            for p, kind in takers[callee].items():
                if (picture := _text(_arg(call, params.index(p), p))) is not None:
                    found.append((name, picture, kind, False))
        for d in f.decorator_list if name in takers else []:
            if not (isinstance(d, ast.Call) and "parametrize" in ast.unparse(d.func) and len(d.args) >= 2):
                continue
            names = _argnames(d.args[0])
            for p, kind in takers[name].items():
                if p not in names:
                    continue
                for case in getattr(d.args[1], "elts", []):
                    value = case.elts[names.index(p)] if len(names) > 1 and isinstance(case, (ast.Tuple, ast.List)) else case
                    if (picture := _text(value)) is not None:
                        found.append((name, picture, kind, True))
    # A helper's own picture is the tests' that use it.
    defs = impact.analyze(text).defs
    tests = sorted(n for n in funcs if n.startswith("test_"))
    shots, problems = {}, []
    for fn, picture, kind, param in found:
        owners = [fn] if fn.startswith("test_") else [t for t in tests if t in impact.close_over(defs, {fn})]
        for owner in owners:
            if picture in shots and shots[picture].test != owner:
                problems.append(f"{picture} is taken by both {shots[picture].test} and {owner}")
            else:
                shots[picture] = Shot(kind, owner, param)
    return shots, problems


def k_expression(pictures, shots):
    """pytest's -k for these pictures: every set-up step ("step" is in their names), and the shots that take the rest
    (a parametrized test's case by the picture's name, which its ID holds)."""
    parts = ["step"]
    for n in sorted(pictures):
        s = shots[n]
        if "step" not in s.test:
            parts.append(n if s.param else s.test)
    return " or ".join(dict.fromkeys(parts))


# ---------------------------------------------------------------- git and the tree

def git(repo, *args, data=None, check=True):
    try:
        r = subprocess.run(["git", "-C", str(repo), "-c", "core.quotepath=false", *args], input=data, capture_output=True)
    except OSError as e:
        raise Refused(f"git can't run here ({e})") from e
    if check and r.returncode != 0:
        raise Refused(f"git {args[0]} failed in {repo}: {r.stderr.decode(errors='replace').strip()}")
    return r


def commit_of(repo, ref):
    if not impact.GIT_REF.match(ref):
        raise Refused(f"not a branch, tag or commit: {ref!r}")
    return git(repo, "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}").stdout.decode().strip()


def is_commit(repo, sha):
    return bool(SHA.match(sha)) and git(repo, "cat-file", "-e", f"{sha}^{{commit}}", check=False).returncode == 0


def changed_between(repo, base, head):
    out = git(repo, "diff", "--name-only", "--no-renames", "-z", base, head).stdout.decode("utf-8", "replace")
    return [p for p in out.split("\0") if p]


def show(repo, sha, path):
    r = git(repo, "show", f"{sha}:{path}", check=False)
    return impact._decode(r.stdout) if r.returncode == 0 else None


def _same(a, b):
    if a == b:
        return True
    ta, tb = impact._decode(a), impact._decode(b)
    return ta is not None and ta == tb   # line endings only


def _walked(path):
    """Whether a tree walk (impact.walk_tree) would see the path: not in build output, caches or a run's output."""
    parts = path.split("/")[:-1]
    return not impact._skipped(path) and not any(
        p in impact.SKIP_DIRS and not (p == "bin" and path.startswith("tests/samples/")) for p in parts)


def _section(lines, section):
    """The [start, end) lines of a file's section: from the first line `from` matches to the next one `to` matches."""
    start_rx, end_rx = section
    start = next((i for i, line in enumerate(lines) if start_rx.search(line)), len(lines))
    end = next((i for i in range(start + 1, len(lines)) if end_rx is not None and end_rx.search(lines[i])), len(lines))
    return start, end


def changed_within(old, new, section):
    """Whether old and new differ within the section (in either version of the file)."""
    a, b = (old or "").splitlines(), (new or "").splitlines()
    (sa, ea), (sb, eb) = _section(a, section), _section(b, section)
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(None, a, b, autojunk=False).get_opcodes():
        if tag != "equal" and ((i1 < ea and i2 > sa) or (j1 < eb and j2 > sb)):
            return True
    return False


def tree_changes(repo, head, tree, wanted):
    """The tree's files that wanted(path) picks and that the head doesn't have, or has otherwise (line endings aside),
    and those the head has that the tree doesn't."""
    listed = git(repo, "ls-tree", "-r", "-z", "--full-tree", head).stdout.decode("utf-8", "replace")
    blobs = {}
    for entry in filter(None, listed.split("\0")):
        meta, path = entry.split("\t", 1)
        _, kind, sha = meta.split()
        if kind == "blob":
            blobs[path] = sha
    files = {p: f for p, f in impact.walk_tree(tree).items() if wanted(p)}
    shas = sorted({blobs[p] for p in files if p in blobs})
    out = git(repo, "cat-file", "--batch", data="".join(s + "\n" for s in shas).encode()).stdout
    contents, at = {}, 0
    for s in shas:
        nl = out.index(b"\n", at)
        size = int(out[at:nl].split()[2])
        contents[s] = out[nl + 1:nl + 1 + size]
        at = nl + 1 + size + 1
    changed = {p for p, f in files.items() if p not in blobs or not _same(contents[blobs[p]], f.read_bytes())}
    gone = {p for p in blobs if p not in files and wanted(p) and _walked(p)}
    return sorted(changed | gone)


def read(path):
    try:
        return Path(path).read_text(encoding="utf-8")
    except FileNotFoundError:
        return None


# ---------------------------------------------------------------- the plan

@dataclass
class Plan:
    head: str
    stale: dict      # picture -> why
    shots: dict
    total: int
    notes: list = field(default_factory=list)   # changes that may change pictures, though they make none stale

    @property
    def k(self):
        return k_expression(self.stale, self.shots) if self.stale else ""


# Files the shots run with that no picture's globs name: a change to one may change pictures, which plan can't tell.
SUPPORT = ("tests/e2e/shots/conftest.py", "tests/e2e/shots/demo.py")


def plan(repo, head_ref, tree, also=(), every=False, since_ref=None):
    """The stale pictures. since_ref: also note what changed since then (a release) that may change pictures without
    making any stale: the set-up steps, whose state many pictures show, and the files in SUPPORT."""
    tree = Path(tree)
    head = commit_of(repo, head_ref)
    if not head:
        raise Refused(f"{head_ref} isn't a commit in {repo}")
    m, problems = load_map(read(tree / MAP) or "")
    if problems:
        raise Refused("pictures.toml: " + "; ".join(problems))
    text = read(tree / SHOTS)
    if text is None:
        raise Refused(f"no {SHOTS} in {tree}")
    shots, problems = shots_of(text)
    if problems:
        raise Refused("test_shots.py: " + "; ".join(problems))
    unknown = sorted(set(also) - set(shots))
    if unknown:
        raise Refused(f"no shot takes {', '.join(unknown)}")
    if every:
        return Plan(head, {n: "every picture asked for" for n in shots}, shots, len(shots))
    taken = load_taken(read(tree / TAKEN))
    regex = {g: impact.glob_regex(g) for g in m.every_glob()}

    def wanted(p):
        return p == SHOTS or p in SUPPORT or p.startswith(IMAGES + "/") or any(rx.match(p) for rx in regex.values())

    # The tree's files that differ from the head's (added, changed or gone) changed after every picture was taken.
    differs = [p for p in tree_changes(repo, head, tree, wanted) if not p.startswith(IMAGES + "/")]
    head_defs = impact.analyze(text).defs
    since, closures = {}, {}

    def counts(base, path):
        """A change to the file since base counts; in [only_within], only one within its section."""
        section = m.section_for(path)
        return section is None or changed_within(show(repo, base, path), read(tree / path), section)

    def changed_shots(base):
        """test_shots.py's top-level names that changed since base, and every one that uses one of them."""
        if base not in closures:
            old = show(repo, base, SHOTS)
            old_defs = impact.analyze(old).defs if old is not None else {}
            changed = {d for d, (src, _) in head_defs.items() if old_defs.get(d, (None,))[0] != src} | (set(old_defs) - set(head_defs))
            closures[base] = impact.close_over(head_defs, changed)
        return closures[base]

    stale = {}
    for n in sorted(shots):
        if n in also:
            stale[n] = "asked for"
            continue
        if not (tree / IMAGES / f"{n}.webp").is_file():
            stale[n] = f"not in {IMAGES}"
            continue
        base = taken.get(n)
        if base is None:
            stale[n] = "never taken (taken.toml doesn't have it)"
            continue
        if not is_commit(repo, base):
            stale[n] = f"taken from {base[:7]}, which {repo} doesn't have"
            continue
        if base not in since:
            since[base] = [p for p in dict.fromkeys(changed_between(repo, base, head) + differs) if counts(base, p)]
        globs = m.globs_for(n, shots[n].kind)
        hit = next((p for p in since[base] for g in globs if regex[g].match(p)), None)
        if hit:
            stale[n] = f"{hit} changed since {base[:7]}"
            continue
        if shots[n].test in changed_shots(base):
            stale[n] = f"its shot ({shots[n].test}) changed since {base[:7]}"
    notes = []
    if since_ref:
        ref = commit_of(repo, since_ref)
        if not ref:
            raise Refused(f"{since_ref} isn't a commit in {repo}")
        steps = sorted(t for t in changed_shots(ref) if t.startswith("test_") and "step" in t)
        if steps:
            notes.append(f"set-up steps changed since {since_ref}: {', '.join(steps)}. A picture that shows what they set "
                         "up isn't stale by that: if it should change, name it with --also")
        changed = set(changed_between(repo, ref, head)) | set(differs)
        notes += [f"{f} changed since {since_ref}: if that changes how pictures look, take them again (--also, or --all)"
                  for f in SUPPORT if f in changed]
    return Plan(head, stale, shots, len(shots), notes)


def record(root, head, names):
    if not SHA.match(head):
        raise Refused(f"--head takes a commit's full SHA: {head!r}")
    bad = [n for n in names if not NAME.match(n)]
    if bad:
        raise Refused(f"not pictures' names: {', '.join(bad)}")
    path = Path(root) / TAKEN
    taken = load_taken(read(path))
    taken.update({n: head for n in names})
    path.write_text(render_taken(taken), encoding="utf-8", newline="\n")


def check(root):
    """(errors, warnings) of the map against the tree."""
    root = Path(root)
    m, errors = load_map(read(root / MAP) or "")
    warnings = []
    text = read(root / SHOTS)
    shots, problems = shots_of(text) if text is not None else ({}, [f"no {SHOTS}"])
    errors += problems
    images = {p.stem for p in (root / IMAGES).glob("*.webp")}
    errors += [f"{IMAGES}/{n}.webp isn't in pictures.toml" for n in sorted(images - set(m.pictures))]
    errors += [f"test_shots.py takes {n}, which isn't in pictures.toml" for n in sorted(set(shots) - set(m.pictures))]
    errors += [f"{n}: no shot in test_shots.py takes it" for n in sorted(set(m.pictures) - set(shots))]
    warnings += [f"{n}: not in {IMAGES} yet: the next run takes it" for n in sorted(set(m.pictures) - images) if n in shots]
    files = list(impact.walk_tree(root))
    for g, where in m.every_glob().items():
        rx = impact.glob_regex(g)
        if not any(rx.match(f) for f in files):
            errors.append(f"{where}: {g} matches no file")
    try:
        taken = load_taken(read(root / TAKEN))
    except Refused as e:
        return errors + [str(e)], warnings
    errors += [f"taken.toml: {n} isn't a picture in pictures.toml" for n in sorted(set(taken) - set(m.pictures))]
    errors += [f"taken.toml: {n} = {v!r} isn't a commit's full SHA" for n, v in sorted(taken.items()) if not SHA.match(str(v))]
    warnings += [f"{n} isn't in taken.toml: the next run takes it" for n in sorted(set(m.pictures) - set(taken))]
    return errors, warnings


# ---------------------------------------------------------------- the command line

def main(argv=None):
    ap = argparse.ArgumentParser(prog="stale.py", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("plan")
    p.add_argument("--git", default=None)
    p.add_argument("--head", default="HEAD")
    p.add_argument("--tree", default=str(ROOT))
    p.add_argument("--also", default="")
    p.add_argument("--all", action="store_true")
    p.add_argument("--since", default=None)
    p.add_argument("--format", choices=("text", "shell"), default="text")
    r = sub.add_parser("record")
    r.add_argument("--head", required=True)
    r.add_argument("--names", required=True)
    r.add_argument("--root", default=str(ROOT))
    c = sub.add_parser("check")
    c.add_argument("--root", default=str(ROOT))
    a = ap.parse_args(argv)
    try:
        if a.cmd == "check":
            errors, warnings = check(a.root)
            for w in warnings:
                print(f"warning: {w}")
            for e in errors:
                print(f"error: {e}")
            print(f"pictures.toml: {'OK' if not errors else f'{len(errors)} error(s)'}")
            return 1 if errors else 0
        if a.cmd == "record":
            names = [n for n in a.names.split(",") if n]
            record(a.root, a.head, names)
            print(f"[e2e] shots: taken.toml says {len(names)} picture(s) were taken from {a.head[:7]}")
            return 0
        also = [n for n in a.also.split(",") if n]
        result = plan(a.git or a.tree, a.head, a.tree, also, a.all, a.since)
        if a.format == "shell":
            for n, why in result.stale.items():
                print(f"[e2e] shots: {n}: {why}", file=sys.stderr)
            for note in result.notes:
                print(f"[e2e] shots: note: {note}", file=sys.stderr)
            print(f"[e2e] shots: {len(result.stale)} of {result.total} picture(s) to take, at {result.head[:7]}", file=sys.stderr)
            only = "" if a.all else ",".join(result.stale)
            print(f"SHOTS_HEAD={shlex.quote(result.head)}")
            print(f"SHOTS_ONLY={shlex.quote(only)}")
            print(f"SHOTS_K={shlex.quote('' if a.all else result.k)}")
        else:
            print(f"{len(result.stale)} of {result.total} picture(s) to take, at {result.head[:7]}")
            for n, why in result.stale.items():
                print(f"  {n}: {why}")
            for note in result.notes:
                print(f"note: {note}")
            if result.stale and not a.all:
                print(f"pytest -k: {result.k}")
        return 0
    except Refused as e:
        print(f"stale.py: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
