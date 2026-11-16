#!/usr/bin/env python3
"""Which end-to-end groups a change needs, and how to run them. The map is tests/e2e/impact.toml (see tests/e2e/README.md).

  impact.py plan [--tier impact|smoke|groups|wide|full|release]                                  (default: impact)
                 [--git BASE | --trees BASE_DIR HEAD_DIR | --diff FILE|- | --changed FILE|-]       (impact needs one)
                 [--groups "02 14 12+slow @t28-paste 31"] [--templates memos,miniflux] [--smoke | --no-smoke]
                 [--no-refine] [--lanes N | --shards N [--template-shards M]] [--timings test|production]
                 [--format text|markdown|json|tsv|matrix] [--out FILE] [--report FILE] [--note TEXT]
                 [--budget MIN] [--root DIR]
      --budget: impact's wall time in all (the map's [budget], 10 min; 0: no limit). Within it, by priority: the core
      smoke (always), one test of each thing the change points at (most specific first), the rest of those groups'
      tests, the rest of the smoke. What doesn't fit is listed as left for the full suite.
      --root: the head without --trees, and git's repo (default: this file's repo).
  impact.py check             the map against the tree: exit 1 on an error; warnings are printed
  impact.py report DIR...     the runs' junit.xml files: one Markdown table, the failures and the minutes
  impact.py minutes DIR...    each group's measured minutes next to the map's, to refresh the map

The change: --git BASE compares the working tree (uncommitted and untracked files too) with BASE's merge base; --trees
compares two folders (no git needed); --diff reads a unified diff; --changed reads a list of paths (each file then gets
its rule's groups, since no hunk is known). The map and the scenarios are read from the head: HEAD_DIR, or this file's
own repo. Python 3.11 or later, standard library only. It never imports a scenario file: it reads them with ast.
"""
from __future__ import annotations

import argparse
import ast
import codecs
import difflib
import json
import os
import re
import subprocess
import sys
import tomllib
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
MAP = "tests/e2e/impact.toml"
SCENARIOS = "tests/e2e/scenarios"
CATALOG = "src/Gitberth/Templates/catalog"
# What a tree walk leaves out (--trees): build output, caches, a run's report and screenshots.
SKIP_DIRS = {".git", "bin", "obj", ".vs", "__pycache__", ".pytest_cache", "node_modules", "TestResults", "App_Data"}
SKIP_PATHS = ("tests/e2e/out",)
RUN_MINUTES = 0.5        # each run's reset and pytest session
NEW_SCENARIO_MINUTES = 5.0
TEMPLATE_SETUP_MINUTES = 1.5   # a template smoke's gallery and Postgres server, before its first template
TIERS = ("impact", "smoke", "groups", "wide", "full", "release")
STRENGTH = ["none", "scenarios", "impact", "smoke", "groups", "wide", "full", "release"]
GROUP_TOKEN = re.compile(r"^\d\d(\+slow)?$")
TEMPLATE_ID = re.compile(r"^[a-z0-9][a-z0-9-]*$")
GIT_REF = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_./@^~{}:+-]*$")
ALL = "all"              # every template
SELF = "@self+importers"


class Refused(Exception):
    """Bad arguments: exit 2, so a workflow input can never become something else."""


class MapError(Exception):
    """The map or the tree can't be read: exit 1."""


# ---------------------------------------------------------------- the map

def glob_regex(glob):
    """A repo-relative glob: "*" and "?" stay within a folder, "**" crosses folders."""
    out, i = [], 0
    while i < len(glob):
        if glob.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif glob.startswith("**", i):
            out.append(".*")
            i += 2
        elif glob[i] == "*":
            out.append("[^/]*")
            i += 1
        elif glob[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(glob[i]))
            i += 1
    return re.compile("^" + "".join(out) + "$")


@dataclass
class Group:
    key: str
    file: str
    minutes: float
    slow: dict
    template_smoke: bool = False
    test_minutes: float | None = None    # measured with the rig's test timings (impact.py minutes --timings test)
    test_slow: dict = field(default_factory=dict)

    @property
    def module(self):
        return self.file[:-3]


@dataclass
class Refine:
    match: str
    regex: re.Pattern | None
    groups: list
    notes: list


@dataclass
class Rule:
    index: int
    globs: list
    matchers: list
    groups: list
    refine: list
    notes: list
    catch_all: bool

    @property
    def none(self):
        return self.groups == ["@none"] and not self.refine

    @property
    def name(self):
        return self.globs[0] + (" ..." if len(self.globs) > 1 else "")

    def matched(self, path):
        """The glob that matched the path, for the report."""
        return next((g for g, m in zip(self.globs, self.matchers) if m.match(path)), self.globs[0])


class ImpactMap:
    def __init__(self, data, problems=None):
        """problems: a list to collect what's wrong (check); without it the first problem raises MapError."""
        self.problems = problems

        def bad(text):
            if problems is None:
                raise MapError(f"{MAP}: {text}")
            problems.append(text)

        self.groups, self.aliases, self.minutes, self.weights, self.rules = {}, {}, {}, {}, []
        for key, g in (data.get("groups") or {}).items():
            if not re.fullmatch(r"\d\d", key) or not isinstance(g, dict) or "file" not in g or "minutes" not in g:
                bad(f"group {key!r} needs a two-digit name, a file and minutes")
                continue
            slow = g.get("slow") or {}
            test = g.get("test_minutes")
            self.groups[key] = Group(key, g["file"], float(g["minutes"]), {n: float(v) for n, v in slow.items()},
                                     bool(g.get("template_smoke")), float(test) if test is not None else None,
                                     {n: float(v) for n, v in (g.get("test_slow") or {}).items()})
        self.test_minutes = {k: float(v) for k, v in (data.get("test_minutes") or {}).items()}
        self.factor = float((data.get("test_timings") or {}).get("factor", 1.0))
        budget = data.get("budget") or {}
        self.budget = float(budget.get("minutes", 0))
        self.setup = float(budget.get("setup", 3.5))
        for name, items in (data.get("aliases") or {}).items():
            if not name.startswith("@") or not isinstance(items, list):
                bad(f"alias {name!r} must start with @ and list groups, aliases or tests")
                continue
            self.aliases[name] = [str(x) for x in items]
        for node, value in (data.get("minutes") or {}).items():
            self.minutes[node] = float(value)
        for tid, value in ((data.get("templates") or {}).get("weight") or {}).items():
            self.weights[tid] = float(value)
        for i, r in enumerate(data.get("rule") or []):
            if not r.get("globs") or not r.get("groups"):
                bad(f"rule {i + 1} needs globs and groups")
                continue
            refine = []
            for e in r.get("refine") or []:
                try:
                    rx = re.compile(e["match"])
                except (re.error, KeyError, TypeError) as err:
                    bad(f"rule {i + 1} ({r['globs'][0]}): refine {e.get('match')!r} doesn't compile: {err}")
                    rx = None
                refine.append(Refine(e.get("match", ""), rx, list(e.get("groups") or []), list(e.get("notes") or [])))
            self.rules.append(Rule(i + 1, list(r["globs"]), [glob_regex(g) for g in r["globs"]], list(r["groups"]),
                                   refine, list(r.get("notes") or []), bool(r.get("catch_all"))))
        self.by_file = {g.file: g for g in self.groups.values()}
        smoke = [g for g in self.groups.values() if g.template_smoke]
        self.template_group = smoke[0] if smoke else None

    @classmethod
    def load(cls, root, problems=None):
        path = Path(root) / MAP
        if not path.is_file():
            path = REPO / MAP   # a head that predates the map: this file's own
        try:
            data = tomllib.loads(path.read_text(encoding="utf-8"))
        except (OSError, tomllib.TOMLDecodeError) as e:
            raise MapError(f"{MAP} doesn't parse: {e}") from e
        return cls(data, problems)

    def rule_for(self, path):
        for r in self.rules:
            if any(m.match(path) for m in r.matchers):
                return r
        return None


# ---------------------------------------------------------------- the scenario files, read with ast

SCENARIO_MODULE = re.compile(r"^test_\d\d\w*$")


@dataclass
class Module:
    """One scenario file: its tests, its top-level names (source text, names each refers to), and its imports."""
    ok: bool = False
    error: str = ""
    tests: list = field(default_factory=list)      # (class or None, function, slow, cases)
    classes: dict = field(default_factory=dict)    # class -> its test functions
    defs: dict = field(default_factory=dict)       # name -> (source text, names it refers to)
    imports: dict = field(default_factory=dict)    # local name -> (scenario module, name)
    others: list = field(default_factory=list)     # other top-level statements' source text


def _slow(decorators):
    return any("mark.slow" in ast.unparse(d) for d in decorators)


def _cases(decorators):
    n = 1
    for d in decorators:
        if isinstance(d, ast.Call) and "parametrize" in ast.unparse(d.func) and len(d.args) >= 2 \
                and isinstance(d.args[1], (ast.List, ast.Tuple)):
            n *= max(1, len(d.args[1].elts))
    return n


def _refs(node):
    names = set()
    for n in ast.walk(node):
        if isinstance(n, ast.Name):
            names.add(n.id)
        elif isinstance(n, ast.arg):   # a parameter: pytest hands a fixture over by its name
            names.add(n.arg)
    return names


def analyze(text):
    m = Module()
    if text is None:
        m.error = "no text"
        return m
    try:
        tree = ast.parse(text)
    except SyntaxError as e:
        m.error = f"doesn't parse (line {e.lineno}: {e.msg})"
        return m
    lines = text.split("\n")
    for node in tree.body:
        start = min([node.lineno] + [d.lineno for d in getattr(node, "decorator_list", [])])
        source = "\n".join(lines[start - 1:node.end_lineno])
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            m.defs[node.name] = (source, _refs(node) - {node.name})
            if node.name.startswith("test_"):
                m.tests.append((None, node.name, _slow(node.decorator_list), _cases(node.decorator_list)))
        elif isinstance(node, ast.ClassDef):
            m.defs[node.name] = (source, _refs(node) - {node.name})
            if node.name.startswith("Test"):
                funcs = []
                for sub in node.body:
                    if isinstance(sub, (ast.FunctionDef, ast.AsyncFunctionDef)) and sub.name.startswith("test_"):
                        funcs.append(sub.name)
                        m.tests.append((node.name, sub.name, _slow(node.decorator_list) or _slow(sub.decorator_list),
                                        _cases(sub.decorator_list)))
                m.classes[node.name] = funcs
        elif isinstance(node, (ast.Assign, ast.AnnAssign, ast.AugAssign)):
            targets = node.targets if isinstance(node, ast.Assign) else [node.target]
            refs = _refs(node.value) if node.value is not None else set()
            for t in targets:
                for n in ast.walk(t):
                    if isinstance(n, ast.Name):
                        m.defs[n.id] = (source, refs - {n.id})
        elif isinstance(node, ast.ImportFrom):
            for a in node.names:
                local = a.asname or a.name
                m.defs[local] = (f"from {'.' * node.level}{node.module} import {a.name} as {local}", set())
                if node.level == 0 and node.module and SCENARIO_MODULE.match(node.module):
                    m.imports[local] = (node.module, a.name)
        elif isinstance(node, ast.Import):
            for a in node.names:
                local = a.asname or a.name.split(".")[0]
                m.defs[local] = (f"import {a.name} as {local}", set())
        elif isinstance(node, ast.Expr) and isinstance(node.value, ast.Constant) and isinstance(node.value.value, str):
            continue   # a docstring
        else:
            m.others.append(source)
    m.ok = True
    return m


def scenario_index(root):
    """Every scenario file of a tree: module name -> Module."""
    folder = Path(root) / SCENARIOS
    return {p.stem: analyze(read_text(p)) for p in sorted(folder.glob("test_*.py"))}


def close_over(defs, changed):
    """The names that changed, plus every top-level definition that refers to one of them, over and over."""
    changed = set(changed)
    grew = True
    while grew:
        grew = False
        for name, (_, refs) in defs.items():
            if name not in changed and refs & changed:
                changed.add(name)
                grew = True
    return changed


# ---------------------------------------------------------------- the change

@dataclass
class Hunk:
    old_start: int
    old_len: int
    new_start: int
    new_len: int
    lines: list = field(default_factory=list)   # (" " | "-" | "+", text)


@dataclass
class Change:
    old_path: str | None
    new_path: str | None
    old_text: str | None = None
    new_text: str | None = None
    hunks: list | None = None    # None: not known (a list of paths, a binary file)
    added: bool = False
    deleted: bool = False
    binary: bool = False
    dashes: bool = False

    @property
    def paths(self):
        return [p for p in dict.fromkeys([self.old_path, self.new_path]) if p]


def read_text(path):
    try:
        data = Path(path).read_bytes()
    except OSError:
        return None
    try:
        return data.decode("utf-8").replace("\r\n", "\n")
    except UnicodeDecodeError:
        return None


def lines_of(text):
    if not text:
        return []
    parts = text.split("\n")
    if parts[-1] == "":
        parts.pop()
    return parts


HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


def _unquote(p):
    if len(p) >= 2 and p[0] == '"' and p[-1] == '"':
        return codecs.escape_decode(p[1:-1].encode("utf-8"))[0].decode("utf-8", "replace")
    return p


def _diff_path(s, prefix):
    s = s.split("\t", 1)[0].rstrip()
    if s == "/dev/null":
        return None
    s = _unquote(s)
    return s[2:] if s.startswith(prefix) else s


def _header_paths(rest):
    if rest.startswith('"'):
        parts = re.findall(r'"(?:[^"\\]|\\.)*"|\S+', rest)
        if len(parts) == 2:
            return _diff_path(parts[0], "a/"), _diff_path(parts[1], "b/")
        return None
    half = (len(rest) - 1) // 2
    if len(rest) % 2 == 1 and rest[half] == " " and rest[:half][2:] == rest[half + 1:][2:]:
        return rest[2:half], rest[half + 3:]
    if " b/" in rest:
        a, b = rest.split(" b/", 1)
        return _diff_path(a, "a/"), b
    return None


def parse_diff(text):
    """A unified diff (git's or plain) -> Changes, with their hunks."""
    changes, cur = [], None
    lines = text.split("\n")
    i, n = 0, len(lines)
    while i < n:
        line = lines[i]
        if line.startswith("diff --git "):
            cur = Change(None, None, hunks=[])
            changes.append(cur)
            paths = _header_paths(line[len("diff --git "):])
            if paths:
                cur.old_path, cur.new_path = paths
            i += 1
            continue
        if line.startswith("--- ") and i + 1 < n and lines[i + 1].startswith("+++ "):
            if cur is None or cur.hunks or cur.dashes:
                cur = Change(None, None, hunks=[])
                changes.append(cur)
            cur.old_path = _diff_path(line[4:], "a/")
            cur.new_path = _diff_path(lines[i + 1][4:], "b/")
            cur.added = cur.added or cur.old_path is None
            cur.deleted = cur.deleted or cur.new_path is None
            cur.dashes = True
            i += 2
            continue
        m = HUNK.match(line)
        if m and cur is not None:
            ol = int(m.group(2)) if m.group(2) is not None else 1
            nl = int(m.group(4)) if m.group(4) is not None else 1
            h = Hunk(int(m.group(1)), ol, int(m.group(3)), nl)
            i += 1
            left_old, left_new = ol, nl
            while i < n and (left_old > 0 or left_new > 0):
                body = lines[i]
                if body.startswith("\\"):
                    i += 1
                    continue
                tag, rest = (body[:1] or " "), body[1:]
                if tag == " ":
                    left_old -= 1
                    left_new -= 1
                elif tag == "-":
                    left_old -= 1
                elif tag == "+":
                    left_new -= 1
                else:
                    break
                h.lines.append((tag, rest))
                i += 1
            while i < n and lines[i].startswith("\\"):
                i += 1
            cur.hunks.append(h)
            continue
        if cur is not None:
            if line.startswith("rename from "):
                cur.old_path = _unquote(line[len("rename from "):])
            elif line.startswith("rename to "):
                cur.new_path = _unquote(line[len("rename to "):])
            elif line.startswith("copy to "):
                cur.new_path = _unquote(line[len("copy to "):])
                cur.added = True
            elif line.startswith("new file mode"):
                cur.added = True
            elif line.startswith("deleted file mode"):
                cur.deleted = True
            elif line.startswith("Binary files ") or line == "GIT binary patch":
                cur.binary = True
        i += 1
    out = []
    for c in changes:
        if c.added:
            c.old_path = None
        if c.deleted:
            c.new_path = None
        if c.binary:
            c.hunks = None
        if c.old_path or c.new_path:
            out.append(c)
    return out


def unified(old_path, new_path, old_text, new_text):
    """A git-style unified diff of one file, from difflib (3 lines of context)."""
    a, b = lines_of(old_text), lines_of(new_text)
    p = new_path or old_path
    head = [f"diff --git a/{old_path or p} b/{new_path or p}"]
    if old_path is None:
        head.append("new file mode 100644")
    if new_path is None:
        head.append("deleted file mode 100644")
    body = list(difflib.unified_diff(a, b, f"a/{old_path}" if old_path else "/dev/null",
                                     f"b/{new_path}" if new_path else "/dev/null", n=3, lineterm=""))
    if not body:
        body = [f"--- a/{old_path}" if old_path else "--- /dev/null", f"+++ b/{new_path}" if new_path else "+++ /dev/null"]
    return "\n".join(head + body) + "\n"


def _skipped(path):
    return any(path == s or path.startswith(s + "/") for s in SKIP_PATHS)


def walk_tree(root):
    root = Path(root)
    files = {}
    for dirpath, dirs, names in os.walk(root):
        rel = Path(dirpath).relative_to(root).as_posix()
        rel = "" if rel == "." else rel

        def keep(d):
            p = f"{rel}/{d}" if rel else d
            if _skipped(p):
                return False
            if d == "bin" and p.startswith("tests/samples/"):
                return True   # the sample apps' own bin/ scripts are part of them
            return d not in SKIP_DIRS

        dirs[:] = sorted(d for d in dirs if keep(d))
        for name in names:
            files[f"{rel}/{name}" if rel else name] = Path(dirpath) / name
    return files


def changes_from_trees(base, head):
    a, b = walk_tree(base), walk_tree(head)
    out = []
    for p in sorted(a.keys() | b.keys()):
        ob = a[p].read_bytes() if p in a else None
        nb = b[p].read_bytes() if p in b else None
        if ob == nb:
            continue
        ot = _decode(ob)
        nt = _decode(nb)
        if ob is not None and nb is not None and ot is not None and ot == nt:
            continue   # line endings only
        old_path = p if p in a else None
        new_path = p if p in b else None
        if (ob is not None and ot is None) or (nb is not None and nt is None):
            out.append(Change(old_path, new_path, binary=True, added=old_path is None, deleted=new_path is None))
            continue
        c = parse_diff(unified(old_path, new_path, ot, nt))[0]
        c.old_text, c.new_text = ot, nt
        out.append(c)
    return out


def _decode(data):
    if data is None:
        return None
    try:
        return data.decode("utf-8").replace("\r\n", "\n")
    except UnicodeDecodeError:
        return None


def _git(root, *args):
    return subprocess.run(["git", "-C", str(root), "-c", "core.quotepath=false", *args], capture_output=True)


class BaseUnknown(Exception):
    pass


def changes_from_git(root, base):
    if not GIT_REF.match(base):
        raise Refused(f"--git takes a branch, tag or commit: {base!r}")
    try:
        mb = _git(root, "merge-base", base, "HEAD")
    except OSError as e:
        raise BaseUnknown(f"git can't run here ({e})") from e
    if mb.returncode != 0:
        raise BaseUnknown(f"git can't find a common ancestor of {base} and HEAD here")
    mb = mb.stdout.decode().strip()
    diff = _git(root, "diff", "-U3", "-M", "--no-color", "--no-ext-diff", "--no-textconv", "--src-prefix=a/",
                "--dst-prefix=b/", mb)
    if diff.returncode != 0:
        raise BaseUnknown(f"git diff failed: {diff.stderr.decode(errors='replace').strip()}")
    changes = parse_diff(diff.stdout.decode("utf-8", "replace"))
    for c in changes:
        if c.old_path and not c.binary:
            shown = _git(root, "show", f"{mb}:{c.old_path}")
            c.old_text = _decode(shown.stdout) if shown.returncode == 0 else None
        if c.new_path and not c.binary:
            c.new_text = read_text(Path(root) / c.new_path)
    others = _git(root, "ls-files", "--others", "--exclude-standard", "-z")
    for p in (x for x in others.stdout.decode("utf-8", "replace").split("\0") if x):
        text = read_text(Path(root) / p)
        if text is None:
            changes.append(Change(None, p, binary=True, added=True))
            continue
        c = parse_diff(unified(None, p, None, text))[0]
        c.new_text = text
        changes.append(c)
    return changes, mb


def reverse_apply(new_text, hunks):
    """The old text, from the new one and the diff's hunks; None when they don't fit."""
    new = lines_of(new_text)
    old, pos = [], 0
    for h in sorted(hunks, key=lambda h: h.new_start):
        at = h.new_start if h.new_len == 0 else h.new_start - 1
        if at < pos:
            return None
        old.extend(new[pos:at])
        seg = [t for tag, t in h.lines if tag in " +"]
        if new[at:at + len(seg)] != seg:
            return None
        old.extend(t for tag, t in h.lines if tag in " -")
        pos = at + len(seg)
    old.extend(new[pos:])
    return "\n".join(old) + ("\n" if old else "")


def changes_from_diff(text, root):
    changes = parse_diff(text)
    for c in changes:
        if c.binary or c.hunks is None:
            continue
        if c.new_path:
            new = read_text(Path(root) / c.new_path)
            if new is not None and (old := reverse_apply(new, c.hunks)) is not None:
                c.new_text = new
                c.old_text = None if c.added else old
        elif c.deleted:
            c.old_text = "\n".join(t for tag, t in (x for h in c.hunks for x in h.lines) if tag in " -") + "\n"
    return changes


def changes_from_list(text, root):
    out = []
    for line in text.splitlines():
        p = line.strip().replace("\\", "/")
        if not p or p.startswith("#"):
            continue
        exists = (Path(root) / p).is_file()
        out.append(Change(p, p if exists else None, new_text=read_text(Path(root) / p) if exists else None))
    return out


def read_input(name):
    if name == "-":
        return sys.stdin.read()
    try:
        return Path(name).read_text(encoding="utf-8")
    except OSError as e:
        raise Refused(f"can't read {name}: {e}") from e


# ---------------------------------------------------------------- the declaration a hunk sits in

CS_KEYWORDS = frozenset("""if else for foreach while do switch case default return throw try catch finally using lock fixed
    await var new yield when get set init add remove base this checked unchecked goto break continue nameof typeof sizeof
    is as in out ref from where select let join orderby group into on equals by ascending descending not and or with
    stackalloc delegate""".split())
CS_MODIFIER = r"(?:public|private|protected|internal|static|async|override|virtual|abstract|sealed|partial|readonly|extern" \
              r"|unsafe|new|required|file|const|volatile)"
CS_TYPE_DECL = re.compile(rf"^\s*(?:\[.*?\]\s*)*(?:{CS_MODIFIER}\s+|ref\s+)*(?:class|struct|interface|enum|record)\b\s*@?\w")
CS_MEMBER = re.compile(rf"^\s*(?:\[.*?\]\s*)*((?:{CS_MODIFIER}\s+)*)([\w@(][\w<>\[\],.?()@\s]*?\s+)?(@?[A-Za-z_]\w*)"
                       r"\s*(?:<[^()]*>)?\s*\(")
CS_FIRST = re.compile(r"^\s*(?:\[.*?\]\s*)*(@?\w+)")
PY_DECL = re.compile(r"^\s*(?:async\s+def|def|class)\s+\w+")
RAZOR_ID = re.compile(r'(?<![\w-])id="[^"]*"')
RAZOR_CODE = re.compile(r"^@(code|functions)\b")


def is_cs_declaration(line):
    """A line that declares a type, a method or a constructor (modifiers, then Name( ), as git's csharp driver sees it."""
    stripped = line.strip()
    if not stripped or stripped.startswith(("//", "/*", "*", "#", "@", "}", "=", ".")):
        return False
    if CS_TYPE_DECL.match(line):
        return True
    m = CS_MEMBER.match(line)
    if not m:
        return False
    first = CS_FIRST.match(line)
    if first and first.group(1) in CS_KEYWORDS:
        return False
    if "=" in line[:m.end()] or m.group(3) in CS_KEYWORDS:
        return False
    return bool(m.group(2)) or bool(m.group(1).strip())   # a constructor needs a modifier; without one it's a call


def _nearest(lines, start, test, stop=0):
    for k in range(min(start, len(lines)) - 1, stop - 1, -1):
        if test(lines[k]):
            return [lines[k]]
    return []


def enclosing(path, lines, start):
    """The nearest declaration above line `start` (0-based): a .cs method, constructor or type; in .razor markup the
    nearest id="..." (inside @code, the nearest C# declaration); a .py def or class."""
    ext = path.rsplit(".", 1)[-1].lower() if "." in path.rsplit("/", 1)[-1] else ""
    if ext == "cs":
        return _nearest(lines, start, is_cs_declaration)
    if ext == "razor":
        code = next((k for k, line in enumerate(lines) if RAZOR_CODE.match(line)), None)
        if code is not None and start > code:
            return _nearest(lines, start, is_cs_declaration, code + 1)
        return _nearest(lines, start, lambda line: RAZOR_ID.search(line) is not None)
    if ext == "py":
        return _nearest(lines, start, lambda line: PY_DECL.match(line) is not None)
    return []


def hunk_texts(change, path):
    """Each hunk's text for the refine words: its changed lines, 3 lines around them, and the declaration it sits in."""
    side_new = change.new_text is not None
    lines = lines_of(change.new_text if side_new else change.old_text)
    out = []
    for h in change.hunks:
        start = (h.new_start if h.new_len == 0 else h.new_start - 1) if side_new else \
            (h.old_start if h.old_len == 0 else h.old_start - 1)
        decl = enclosing(path, lines, start) if lines else []
        out.append("\n".join([t for _, t in h.lines] + decl))
    return out


# ---------------------------------------------------------------- choosing

@dataclass
class FileReason:
    path: str
    rule: Rule | None
    tokens: set
    how: str
    notes: list
    templates: object = None      # for 31: a set of template ids, or ALL
    scenario: bool = False        # a tests/e2e/scenarios/test_*.py file
    catch_all: bool = False
    refined: bool = False         # every hunk hit a refine word: the most specific choice
    own: set = field(default_factory=set)   # a scenario file's own group (and its changed slow test)

    def rank(self, token):
        """How directly the change points at what a token selects (lower first): a refine hit or a scenario's own group,
        then the rule's own groups in the map's order (specific files before folders), then @wide."""
        if self.scenario:
            return (0, 0) if token in self.own else (1, 0)
        index = self.rule.index if self.rule else 9999
        if token == "@wide" or self.catch_all:
            return (2, index)
        return (0 if self.refined else 1, index)


@dataclass
class Selection:
    groups: dict = field(default_factory=dict)    # group -> the slow tests it keeps
    nodes: set = field(default_factory=set)       # single tests (node IDs)
    templates: object = None                       # None, ALL, or a set of template ids
    aliases: set = field(default_factory=set)     # every alias expanded
    new_files: dict = field(default_factory=dict)  # a scenario file the map doesn't know yet -> minutes

    def add_templates(self, t):
        if t is None:
            return
        if self.templates == ALL or t == ALL:
            self.templates = ALL
        else:
            self.templates = set(self.templates or set()) | set(t)


class Planner:
    def __init__(self, head, imap, index, templates_all):
        self.head = Path(head)
        self.map = imap
        self.index = index
        self.catalog = templates_all
        self.warnings, self.notes = [], []

    def warn(self, text):
        if text not in self.warnings:
            self.warnings.append(text)

    def note(self, text):
        if text not in self.notes:
            self.notes.append(text)

    # ---- tokens -> selection

    def expand(self, tokens, sel, templates=ALL, stack=()):
        for tok in tokens:
            if tok.startswith("@"):
                if tok == SELF:
                    continue
                if tok in stack:
                    raise MapError(f"alias {tok} contains itself")
                if tok not in self.map.aliases:
                    raise MapError(f"no alias {tok}")
                sel.aliases.add(tok)
                self.expand(self.map.aliases[tok], sel, templates, stack + (tok,))
            elif "::" in tok:
                sel.nodes.add(tok)
            elif tok.startswith("new:"):
                sel.new_files[tok[4:]] = NEW_SCENARIO_MINUTES
            elif GROUP_TOKEN.match(tok):
                key = tok[:2]
                g = self.map.groups.get(key)
                if g is None:
                    raise MapError(f"no group {key}")
                if g.template_smoke:
                    sel.add_templates(templates)
                    continue
                kept = sel.groups.setdefault(key, set())
                if tok.endswith("+slow"):
                    kept |= set(g.slow)
            else:
                raise MapError(f"{tok!r} is neither a group, an alias nor a test")

    def normalize(self, sel):
        """A single test whose whole group runs is dropped; a slow one stays in its group instead of being deselected.
        A class covers its own tests."""
        for node in sorted(sel.nodes):
            parts = node.split("::")
            g = self.map.by_file.get(parts[0])
            if g is None:
                continue
            if g.template_smoke:
                sel.nodes.discard(node)
                self.warn(f"{node}: the template smoke runs on its own (31, or --groups 31 --templates ...)")
                sel.add_templates(ALL)
                continue
            if g.key in sel.groups:
                if len(parts) == 2 and parts[1] in g.slow:
                    sel.groups[g.key].add(parts[1])
                sel.nodes.discard(node)
        for node in sorted(sel.nodes):
            if any(node.startswith(other + "::") for other in sel.nodes if other != node):
                sel.nodes.discard(node)

    # ---- per changed file

    def file_reasons(self, change, no_refine):
        out = []
        paths = [p for p in change.paths if not _skipped(p)]
        rules = [(p, self.map.rule_for(p)) for p in paths]
        if len(rules) == 2 and rules[0][1] is rules[1][1]:
            rules = [(f"{paths[0]} -> {paths[1]}", rules[1][1])]
        for p, rule in rules:
            name = p.split(" -> ")[-1]
            if rule is None:
                self.warn(f"no rule matches {name}: add one to {MAP} (it runs @wide meanwhile)")
                out.append(FileReason(p, None, {"@wide"}, "no rule: @wide", [], catch_all=True))
                continue
            if rule.catch_all:
                self.warn(f"only the catch-all matches {name}: add a rule to {MAP} (it runs @wide meanwhile)")
            if rule.groups == [SELF]:
                out.append(self.scenario_reason(p, change))
                continue
            glob = rule.matched(name)
            tokens, how, notes = set(rule.groups), f"rule {glob}", list(rule.notes)
            if rule.catch_all:
                how = f"no rule but the catch-all ({glob}): add one"
            elif rule.refine and not no_refine and change.hunks:
                words, fell_back, tokens = [], False, set()
                for text in hunk_texts(change, name):
                    hits = [e for e in rule.refine if e.regex and e.regex.search(text)]
                    if not hits:
                        tokens |= set(rule.groups)
                        fell_back = True
                        continue
                    for e in hits:
                        tokens |= set(e.groups)
                        notes += [x for x in e.notes if x not in notes]
                        words += [w for w in dict.fromkeys(m.group(0) for m in e.regex.finditer(text)) if w not in words]
                if words:
                    how = "hunk words: " + ", ".join(words[:8]) + (" ..." if len(words) > 8 else "")
                    if fell_back:
                        how += f"; other hunks: rule {glob}"
                else:
                    how = f"rule {glob} (no hunk word)"
            elif rule.refine and change.hunks is None:
                how = f"rule {glob} (hunks unknown)"
            elif rule.refine and no_refine:
                how = f"rule {glob} (--no-refine)"
            templates = ALL
            m = re.fullmatch(re.escape(CATALOG) + r"/([a-z0-9-]+)\.yaml", name)
            if m:
                templates = {m.group(1)}
            refined = how.startswith("hunk words") and "other hunks" not in how
            out.append(FileReason(p, rule, tokens - {"@none"}, how, notes, templates, catch_all=rule.catch_all,
                                  refined=refined))
        return out

    def scenario_reason(self, p, change):
        """@self+importers: the file's own group, plus each group that imports a name it changed."""
        path = (change.new_path or change.old_path)
        file = path.rsplit("/", 1)[-1]
        mod = file[:-3]
        g = self.map.by_file.get(file)
        tokens = set()
        parts = []
        if change.new_path is None:
            if g:
                self.warn(f"{file} is gone, but {MAP} still has its group {g.key}")
        elif g:
            tokens.add(g.key)
        else:
            tokens.add(f"new:{file}")
            self.warn(f"{file} has no group in {MAP}: it runs as itself (about {NEW_SCENARIO_MINUTES:g} min); add its group")
        new = analyze(change.new_text) if change.new_text is not None else self.index.get(mod)
        old = analyze(change.old_text) if change.old_text is not None else None
        direct = sorted(o for o, b in self.index.items() if o != mod and b.ok and any(src == mod for src, _ in b.imports.values()))
        if new is not None and not new.ok and change.new_path is not None:
            self.warn(f"{file} {new.error}")
        why = None
        if change.new_path is None:
            why = "it's gone"
        elif old is None:
            why = "its old text isn't known" if not change.added else None
        elif not old.ok or new is None or not new.ok:
            why = "it doesn't parse"
        elif old.others != new.others:
            why = "a top-level statement changed"
        own = set(tokens)
        if why is not None or (change.added and old is None):
            for o in direct:
                tokens.add(self._group_token(o))
            importers = " ".join(self._group_name(o) for o in direct) or "none"
            how = f"{why}: its own group and every importer ({importers})" if why else \
                f"new file: its own group and its importers ({importers})"
            return FileReason(p, None, tokens, how, [], scenario=True, own=own)
        changed = {n for n in old.defs.keys() | new.defs.keys() if old.defs.get(n, (None,))[0] != new.defs.get(n, (None,))[0]}
        mine = close_over(new.defs, changed)
        if g:
            for t in sorted(g.slow):
                if t in mine:   # a slow test whose own code (or a helper it uses) changed runs too
                    tokens.add(f"{g.file}::{t}")
                    own.add(f"{g.file}::{t}")
                    parts.append("so its slow test runs" if t in changed else f"which its slow test {t} uses")
        seen = {mod: set(mine)}
        queue = [(mod, set(mine))]
        hits = {}
        while queue:
            src_mod, names = queue.pop(0)
            for other, b in sorted(self.index.items()):
                if other == src_mod or other == mod or not b.ok:
                    continue
                got = {local for local, (src, name) in b.imports.items() if src == src_mod and name in names}
                if not got:
                    continue
                hits.setdefault(other, set()).update(got)
                more = close_over(b.defs, got) - seen.setdefault(other, set())
                if more:
                    seen[other] |= more
                    queue.append((other, more))
        for other in hits:
            tokens.add(self._group_token(other))
        shown = ", ".join(sorted(changed)[:6]) + (" ..." if len(changed) > 6 else "") if changed else "no top-level name"
        imp = " ".join(self._group_name(o) for o in sorted(hits))
        how = f"changed: {shown}" + (f", {parts[0]}" if parts else "") + \
            (f"; imported by {imp}" if imp else "; nothing imports those names")
        return FileReason(p, None, tokens, how, [], scenario=True, own=own)

    def _group_token(self, module):
        g = self.map.by_file.get(module + ".py")
        return g.key if g else f"new:{module}.py"

    def _group_name(self, module):
        g = self.map.by_file.get(module + ".py")
        return g.key if g else module


# ---------------------------------------------------------------- runs

@dataclass
class Item:
    order: tuple
    file: str
    nodes: list
    deselect: list
    minutes: float
    group: str | None = None
    slow_of: str | None = None

    def args(self):
        out = list(self.nodes) if self.nodes else [self.file]
        for d in self.deselect:
            out += ["--deselect", d]
        return out


@dataclass
class Run:
    slot: int                   # lane or shard number
    kind: str                   # "lane", "shard" or "templates"
    timings: str
    templates: list             # [] for every template (or no template smoke)
    items: list
    minutes: float
    template_run: bool = False

    def args(self):
        if self.template_run:
            return ["-k", "test_31_template_smoke"]
        out = []
        for it in self.items:
            out += it.args()
        return out


def group_order(key):
    return (int(key[:2]) if key[:2].isdigit() else 99, key)


@dataclass
class Unit:
    """What a time budget takes or leaves: a test function, a test class (its fixture runs once for all its tests), a
    slow test, a new scenario file, or a template smoke run."""
    node: str
    file: str
    minutes: float
    group: str | None = None
    slow: bool = False
    templates: list | None = None    # a template smoke run's templates ([]: every one)


class Builder:
    def __init__(self, imap, index, catalog, timings="test"):
        self.map, self.index, self.catalog, self.timings = imap, index, catalog, timings

    # ---- minutes, under the run's timings: the map's minutes are production timings'; with test timings a group's
    # measured test_minutes, else the map's factor times its minutes

    def ratio(self, g):
        if self.timings != "test":
            return 1.0
        if g is not None and g.test_minutes is not None and g.minutes > 0:
            return g.test_minutes / g.minutes
        return self.map.factor

    def group_minutes(self, g):
        if self.timings == "test" and g.test_minutes is not None:
            return g.test_minutes
        return g.minutes * self.ratio(g)

    def slow_minutes(self, g, t):
        if self.timings == "test":
            return g.test_slow.get(t, g.slow[t] * self.map.factor)
        return g.slow[t]

    def named_minutes(self, node, g):
        if self.timings == "test" and node in self.map.test_minutes:
            return self.map.test_minutes[node]
        if node in self.map.minutes:
            return self.map.minutes[node] * self.ratio(g)
        return None

    def cases(self, g, node):
        mod = self.index.get(g.module)
        if mod is None or not mod.ok:
            return 1
        parts = node.split("::")
        n = 0
        for c, f, slow, k in mod.tests:
            if slow:
                continue
            if (len(parts) == 2 and (c == parts[1] or (c is None and f == parts[1]))) or \
                    (len(parts) == 3 and c == parts[1] and f == parts[2]):
                n += k
        return max(n, 1)

    def units(self, g):
        """A group's tests one by one, in file order: each test function, and each test class whole; slow tests left
        out. Their minutes: the map's for the tests it names, and the rest share what's left of the group's."""
        mod = self.index.get(g.module)
        if mod is None or not mod.ok:
            return [Unit(g.file, g.file, self.group_minutes(g), g.key)]
        nodes = []
        for c, f, slow, _ in mod.tests:
            node = f"{g.file}::{c or f}"
            if not slow and node not in nodes:
                nodes.append(node)
        named = {n: m for n in nodes if (m := self.named_minutes(n, g)) is not None}
        rest = sum(self.cases(g, n) for n in nodes if n not in named)
        share = max(0.05, (self.group_minutes(g) - sum(named.values())) / rest) if rest else 0.0
        return [Unit(n, g.file, named.get(n, share * self.cases(g, n)), g.key) for n in nodes]

    def node_minutes(self, node):
        parts = node.split("::")
        g = self.map.by_file.get(parts[0])
        if g is None:
            return NEW_SCENARIO_MINUTES
        if len(parts) == 2 and parts[1] in g.slow:
            return self.slow_minutes(g, parts[1])
        named = self.named_minutes(node, g)
        if named is not None:
            return named
        units = {u.node: u.minutes for u in self.units(g)}
        if node in units:
            return units[node]
        outer = "::".join(parts[:2])   # a test in a class: its share of the class
        return units.get(outer, 0.5) * self.cases(g, node) / self.cases(g, outer)

    def items(self, sel):
        items = []
        for key in sorted(sel.groups, key=group_order):
            g = self.map.groups[key]
            kept = sel.groups[key]
            # Every slow test is deselected from the group's item; one it keeps is an item of its own, so it can go to
            # another lane or shard (merge_slow puts it back when it lands in the group's run).
            deselect = [f"{g.file}::{t}" for t in sorted(g.slow)]
            items.append(Item(group_order(key) + (0, ""), g.file, [], deselect, self.group_minutes(g), group=key))
            for t in sorted(kept):
                items.append(Item(group_order(key) + (1, t), g.file, [f"{g.file}::{t}"], [], self.slow_minutes(g, t),
                                  slow_of=key))
        by_file = {}
        for node in sorted(sel.nodes):
            by_file.setdefault(node.split("::")[0], []).append(node)
        for file, nodes in by_file.items():
            g = self.map.by_file.get(file)
            key = g.key if g else file
            plain = [n for n in nodes if not (g and len(n.split("::")) == 2 and n.split("::")[1] in g.slow)]
            if plain:
                items.append(Item(group_order(key) + (0, ""), file, plain, [], sum(self.node_minutes(n) for n in plain)))
            for n in nodes:
                if n not in plain:
                    items.append(Item(group_order(key) + (1, n), file, [n], [], self.node_minutes(n)))
        for file, minutes in sorted(sel.new_files.items()):
            items.append(Item((99, file, 0, ""), file, [], [], minutes))
        return items

    def template_minutes(self, ids):
        """The group's minutes for every template; a share by weight for some, plus the smoke's own setup (the gallery,
        a Postgres server)."""
        g = self.map.template_group
        total = g.minutes if g else 20.0
        if ids is None:
            return total
        setup = min(TEMPLATE_SETUP_MINUTES, total)
        all_w = sum(self.map.weights.get(t, 1.0) for t in self.catalog) or 1.0
        return setup + (total - setup) * sum(self.map.weights.get(t, 1.0) for t in ids) / all_w

    def template_runs(self, templates, shards):
        if templates is None:
            return []
        if templates == ALL and shards <= 1:
            return [([], self.template_minutes(None))]
        ids = sorted(self.catalog) if templates == ALL else sorted(templates)
        if not ids:
            return []
        bins = [[0.0, []] for _ in range(max(1, min(shards, len(ids))))]
        for t in sorted(ids, key=lambda t: (-self.map.weights.get(t, 1.0), t)):
            b = min(range(len(bins)), key=lambda k: (bins[k][0], k))
            bins[b][0] += self.map.weights.get(t, 1.0)
            bins[b][1].append(t)
        return [(sorted(b[1]), self.template_minutes(b[1])) for b in bins if b[1]]

    def pack(self, items, slots, start=None):
        loads = list(start or [0.0] * slots)
        used = [False] * slots
        placed = [[] for _ in range(slots)]
        for it in sorted(items, key=lambda it: (-it.minutes, it.order)):
            k = min(range(slots), key=lambda s: (loads[s] + (0 if used[s] else RUN_MINUTES), s))
            if not used[k]:
                loads[k] += RUN_MINUTES
                used[k] = True
            loads[k] += it.minutes
            placed[k].append(it)
        return [merge_slow(sorted(p, key=lambda it: it.order)) for p in placed]

    def runs(self, sel, lanes, shards, template_shards, timings):
        items = self.items(sel)
        tpl = self.template_runs(sel.templates, template_shards if shards else 1)
        runs = []
        if shards:
            for k, placed in enumerate(self.pack(items, shards)):
                if placed:
                    runs.append(Run(k + 1, "shard", timings, [], placed, RUN_MINUTES + sum(i.minutes for i in placed)))
            for k, (ids, minutes) in enumerate(tpl):
                runs.append(Run(k + 1, "templates", "production", ids, [], RUN_MINUTES + minutes, template_run=True))
            return runs
        start = [0.0] * lanes
        if tpl:
            start[0] += sum(RUN_MINUTES + m for _, m in tpl)   # the template smoke stays on the first rig, by its images
        for k, placed in enumerate(self.pack(items, lanes, start)):
            if placed:
                runs.append(Run(k + 1, "lane", timings, [], placed, RUN_MINUTES + sum(i.minutes for i in placed)))
        for ids, minutes in tpl:
            runs.append(Run(1, "lane", "production", ids, [], RUN_MINUTES + minutes, template_run=True))
        return runs

    def budget_runs(self, sel, order, core, rest_of_smoke, lanes, cap):
        """The runs within a time budget: each lane takes at most cap minutes. By priority: the core smoke (always in,
        whatever it costs); then one test of each thing the change points at, most specific first (`order`); then the
        rest of those groups' tests, one by one; then the rest of the smoke. Returns the runs and what was left out."""
        group_units = {}
        for key in sel.groups:
            g = self.map.groups[key]
            group_units[key] = self.units(g) + [Unit(f"{g.file}::{t}", g.file, self.slow_minutes(g, t), key, slow=True)
                                                for t in sorted(sel.groups[key])]

        def unit_of(node):
            parts = node.split("::")
            g = self.map.by_file.get(parts[0])
            if g and g.key in group_units:
                for u in group_units[g.key]:
                    if u.node in (node, "::".join(parts[:2])):   # a test in a class runs with its class
                        return u
            slow = bool(g and len(parts) == 2 and parts[1] in g.slow)
            return Unit(node, parts[0], self.node_minutes(node), g.key if g else None, slow=slow)

        cands = [(unit_of(n), True) for n in core]
        firsts, rests = [], []
        for kind, name in order:
            if kind == "group" and name in group_units:
                firsts.append(group_units[name][0])
                rests += group_units[name][1:]
            elif kind == "node" and name in sel.nodes:
                firsts.append(unit_of(name))
            elif kind == "file" and name in sel.new_files:
                firsts.append(Unit(name, name, sel.new_files[name]))
            elif kind == "templates" and sel.templates is not None:
                ids = [] if sel.templates == ALL else sorted(sel.templates)
                firsts.append(Unit("the template smoke", "", self.template_minutes(ids or None), templates=ids))
        cands += [(u, False) for u in firsts + rests]
        cands += [(unit_of(n), False) for n in rest_of_smoke
                  if n in sel.nodes or (self.map.by_file.get(n.split("::")[0]) and
                                        self.map.by_file[n.split("::")[0]].key in group_units)]
        load, used, where, home, left = [0.0] * lanes, [False] * lanes, {}, {}, []
        for u, forced in cands:
            if u.node in where or any(x.node == u.node for x in left):
                continue
            tries = [0] if u.templates is not None else \
                sorted(range(lanes), key=lambda k: (k != home.get(u.group, -1), load[k], k))
            for k in tries:
                need = u.minutes + (RUN_MINUTES if u.templates is not None or not used[k] else 0)
                if forced or load[k] + need <= cap + 1e-9:
                    load[k] += need
                    used[k] = used[k] or u.templates is None
                    where[u.node] = (k, u)
                    if u.group:
                        home.setdefault(u.group, k)
                    break
            else:
                left.append(u)
        runs = []
        for k in range(lanes):
            mine = [u for s, u in where.values() if s == k and u.templates is None]
            if not mine:
                continue
            items = []
            for key in sorted({u.group for u in mine if u.group in group_units}, key=group_order):
                g = self.map.groups[key]
                here = {u.node for u in mine if u.group == key}
                plain = [u for u in group_units[key] if not u.slow]
                if all(u.node in here for u in plain):   # the whole group is in this lane: run its file
                    taken = [u for u in group_units[key] if u.node in here]
                    deselect = [f"{g.file}::{t}" for t in sorted(g.slow) if f"{g.file}::{t}" not in here]
                    items.append(Item(group_order(key) + (0, ""), g.file, [], deselect, sum(u.minutes for u in taken),
                                      group=key))
                    mine = [u for u in mine if u.group != key]
            by_file = {}
            for u in mine:
                by_file.setdefault(u.file, []).append(u)
            for file, us in by_file.items():
                g = self.map.by_file.get(file)
                pos = {u.node: i for i, u in enumerate(group_units.get(g.key, []))} if g else {}
                us.sort(key=lambda u: (pos.get(u.node, 999), u.node))
                items.append(Item(group_order(g.key if g else file) + (0, ""), file, [u.node for u in us] if g else [], [],
                                  sum(u.minutes for u in us)))
            items.sort(key=lambda it: it.order)
            runs.append(Run(k + 1, "lane", self.timings, [], items, RUN_MINUTES + sum(it.minutes for it in items)))
        for s, u in where.values():
            if u.templates is not None:
                runs.append(Run(1, "lane", "production", u.templates, [], RUN_MINUTES + u.minutes, template_run=True))
        return runs, left


def left_out_text(left, builder, sel, smoke=()):
    """What a budget left for the full suite, by group: "21 (all 5 tests)", "02: TestClosed..., test_x"; the smoke's
    own tests as the smoke."""
    out, by_group, smoked = [], {}, []
    for u in left:
        if u.templates is not None:
            out.append(f"the template smoke ({', '.join(u.templates) or 'every template'})")
        elif u.node in smoke and u.group not in sel.groups:
            smoked.append(u)
        elif u.group is None:
            out.append(u.node)
        else:
            by_group.setdefault(u.group, []).append(u)
    if smoked:
        out.append("the rest of the smoke: " + ", ".join(u.node for u in smoked) + f" ({sum(u.minutes for u in smoked):.1f} min)")
    for key in sorted(by_group, key=group_order):
        us = by_group[key]
        g = builder.map.groups[key]
        plain = [u for u in builder.units(g)] if key in sel.groups else []
        names = [u.node.split("::", 1)[1] if "::" in u.node else u.node for u in us]
        minutes = sum(u.minutes for u in us)
        if plain and len([u for u in us if not u.slow]) == len(plain):
            out.append(f"{key} (the whole group, {minutes:.1f} min)")
        else:
            out.append(f"{key}: {', '.join(names)} ({minutes:.1f} min)")
    return out


def merge_slow(items):
    """A slow test that landed in its own group's run goes back into the group: no --deselect, no item of its own."""
    groups = {it.group: it for it in items if it.group}
    out = []
    for it in items:
        if it.slow_of and it.slow_of in groups:
            g = groups[it.slow_of]
            g.deselect = [d for d in g.deselect if d != it.nodes[0]]
            g.minutes += it.minutes
            continue
        out.append(it)
    return out


# ---------------------------------------------------------------- the plan

@dataclass
class Plan:
    map: ImpactMap
    tier: str
    asked: str
    reasons: list
    sel: Selection
    smoke: bool
    runs: list
    lanes: int
    shards: int
    warnings: list
    notes: list
    timings: str
    base: str = ""
    budget: float = 0.0           # minutes of wall time (0: no limit)
    left_out: list = field(default_factory=list)   # what the budget left for the full suite
    left_tests: int = 0           # how many tests (or runs) that is

    def summary(self):
        """One line: what runs, e.g. "04 21 26 + smoke (7 tests)"."""
        if not self.runs:
            return "nothing in the rig"
        smoke = set(self.map.aliases.get("@smoke", [])) | set(self.map.aliases.get("@core", []))
        parts, singles, smoked = [], 0, 0
        for r in self.runs:
            for it in r.items:
                if it.group:
                    every = self.map.groups[it.group].slow
                    parts.append((it.group, it.group + ("+slow" if every and not it.deselect else "")))
                    continue
                for n in it.nodes or [it.file]:
                    if self.smoke and n in smoke:
                        smoked += 1
                    else:
                        singles += 1
        text = " ".join(name for _, name in sorted(set(parts), key=lambda p: group_order(p[0])))
        if smoked:
            text += (" + " if text else "") + f"smoke ({smoked} test{'s' if smoked != 1 else ''})"
        if singles:
            text += (" + " if text else "") + f"{singles} single test{'s' if singles != 1 else ''}"
        for r in self.runs:
            if r.template_run:
                text += (" + " if text else "") + f"template smoke ({', '.join(r.templates) or 'every template'})"
        return text

    def minutes(self):
        return sum(r.minutes for r in self.runs)

    def loads(self):
        loads = {}
        for r in self.runs:
            loads[r.slot] = loads.get(r.slot, 0.0) + r.minutes
        return loads

    def wall(self):
        """Minutes in all: the unit tests beside the rigs' setup, then the longest lane."""
        return self.map.setup + max(self.loads().values(), default=0.0)

    def headline(self):
        text = self.tier + (f" (asked for {self.asked})" if self.asked not in ("impact", self.tier) else "")
        if self.left_out:
            n = self.left_tests or len(self.left_out)
            text += f", cut to the {self.budget:g}-minute budget: {n} test{'s' if n != 1 else ''} left for the full suite"
        return text

    def slow_left_out(self):
        return [f"{key} {t}" for key in sorted(self.sel.groups, key=group_order)
                for t in sorted(self.map.groups[key].slow) if t not in self.sel.groups[key]]


def slot_name(run, lanes):
    if run.kind == "lane":
        if lanes == 1:
            return "lane 1 (one rig: E2E_INSTANCE picks it)"
        return f"lane {run.slot} ({['first rig', 'rig b', 'rig c'][run.slot - 1]})"
    if run.kind == "templates":
        return f"template shard {run.slot}"
    return f"shard {run.slot}"


def estimate_line(plan):
    total = plan.minutes()
    timings = "with test timings" if plan.timings == "test" else "with production timings"
    if not plan.runs:
        return f"estimate: about {plan.map.setup:.1f} min in all: the unit tests; nothing in the rig"
    if plan.shards:
        per = sorted((r.minutes for r in plan.runs), reverse=True)
        return (f"estimate ({timings}): {total:.1f} min of pytest time in {len(plan.runs)} jobs (longest {per[0]:.1f} min), "
                f"plus about 6-8 min of setup per job")
    loads = plan.loads()
    lanes = " + ".join(f"{loads[k]:.1f}" for k in sorted(loads))
    word = "lane" if len(loads) == 1 else "lanes"
    budget = f", within the {plan.budget:g}-minute budget" if plan.budget else ""
    return (f"estimate ({timings}): about {plan.wall():.1f} min in all{budget}: {plan.map.setup:.1f} for the unit tests "
            f"beside the rigs' setup, then {len(loads)} {word} of {lanes} min")


def text_report(plan, run_hint=True):
    out = [f"E2E plan: {plan.headline()}", "  unit tests: always, first (dotnet test tests/Gitberth.Tests)"]
    if plan.base:
        out.append(f"  compared with: {plan.base}")
    if plan.reasons:
        out.append("  why:")
        none = [r for r in plan.reasons if not r.tokens]
        shown = [r for r in plan.reasons if r.tokens] + (none if len(none) <= 6 else [])
        width = min(60, max(len(r.path) for r in shown) if shown else 10)
        for r in shown:
            groups = " ".join(sorted(r.tokens, key=lambda t: (t.startswith("@"), "::" in t, t))) or "none"
            groups = re.sub(r"test_\d\d\w*\.py::(\w+::)?", "", groups)
            out.append(f"    {r.path.ljust(width)}  {groups.ljust(14)}  {r.how}")
        if len(none) > 6:
            out.append(f"    and {len(none)} more files that need nothing in the rig ({', '.join(r.path for r in none[:3])} ...)")
    out.append(f"  runs: {plan.summary()}")
    if plan.left_out:
        out.append("  left for the full suite (the budget):")
        out += [f"    - {x}" for x in plan.left_out]
    slow_out = plan.slow_left_out()
    out.append("  slow tests left out: " + ("; ".join(slow_out) if slow_out else "none"))
    out.append("  notes:" + (" none" if not plan.notes else ""))
    out += [f"    - {n}" for n in plan.notes]
    out.append("  warnings:" + (" none" if not plan.warnings else ""))
    out += [f"    - {w}" for w in plan.warnings]
    out.append("  " + estimate_line(plan))
    for r in plan.runs:
        what = f"template smoke ({', '.join(r.templates) or 'every template'})" if r.template_run else f"{len(r.items)} item(s)"
        out.append(f"  {slot_name(r, plan.lanes)}: {r.minutes:.1f} min, {r.timings} timings, {what}")
        if r.template_run:
            out.append("      -k test_31_template_smoke" + (f"   E2E_TEMPLATES={','.join(r.templates)}" if r.templates else ""))
        for it in r.items:
            out.append("      " + " ".join(it.args()))
    if run_hint and plan.runs:
        out.append("  run it: sh tests/e2e/run.sh " + {"impact": "impact --base <ref>", "groups": "groups <groups>"}.get(plan.asked, plan.asked)
                   + (f" --lanes {plan.lanes}" if plan.lanes > 1 else "") + "   (on CT 101: e2e-run.ps1 -Tree <tree>"
                   + ("" if plan.asked == "impact" else f" -Tier {plan.asked}") + ")")
    return "\n".join(out) + "\n"


def markdown_report(plan):
    out = [f"### E2E plan: {plan.headline()}", ""]
    if plan.base:
        out.append(f"Compared with `{plan.base}`. The unit tests always run first.")
        out.append("")
    if plan.reasons:
        out += ["| File | Groups | Why |", "|---|---|---|"]
        for r in plan.reasons:
            groups = " ".join(sorted(r.tokens)) or "none"
            groups = re.sub(r"test_\d\d\w*\.py::(\w+::)?", "", groups)
            out.append(f"| `{r.path}` | {groups} | {r.how.replace('|', '/')} |")
        out.append("")
    out.append(f"**Runs:** {plan.summary()}  ")
    out.append(f"**{estimate_line(plan)[0].upper()}{estimate_line(plan)[1:]}**")
    if plan.left_out:
        out += ["", "**Left for the full suite (the budget):**"] + [f"- {x}" for x in plan.left_out]
    if plan.notes:
        out += ["", "**Notes:**"] + [f"- {n}" for n in plan.notes]
    if plan.warnings:
        out += ["", "**Warnings:**"] + [f"- {w}" for w in plan.warnings]
    if plan.runs:
        out += ["", "<details><summary>The runs</summary>", "", "```"]
        for r in plan.runs:
            out.append(f"{slot_name(r, plan.lanes)}, {r.timings} timings, {r.minutes:.1f} min"
                       + (f", E2E_TEMPLATES={','.join(r.templates)}" if r.templates else ""))
            out.append("  " + " ".join(r.args()))
        out += ["```", "", "</details>"]
    out.append("")
    return "\n".join(out) + "\n"


def run_name(r):
    if r.template_run:
        return f"templates {r.slot}: " + (" ".join(r.templates) if r.templates else "every template")
    groups = [it.group for it in r.items if it.group]
    singles = len(r.items) - len(groups)
    name = " ".join(groups)
    if singles:
        name += (" + " if name else "") + f"{singles} more"
    return f"e2e {r.slot}: {name}"


def json_plan(plan):
    return {
        "tier": plan.tier,
        "asked": plan.asked,
        "base": plan.base,
        "summary": plan.summary(),
        "reasons": [{"path": r.path, "groups": sorted(r.tokens), "how": r.how, "notes": r.notes} for r in plan.reasons],
        "warnings": plan.warnings,
        "notes": plan.notes,
        "selection": {
            "groups": {k: sorted(v) for k, v in sorted(plan.sel.groups.items())},
            "tests": sorted(plan.sel.nodes),
            "smoke": plan.smoke,
            "templates": plan.sel.templates if plan.sel.templates in (None, ALL) else sorted(plan.sel.templates),
            "new_files": sorted(plan.sel.new_files),
        },
        "budget": plan.budget,
        "left_out": plan.left_out,
        "estimate": {"minutes": round(plan.minutes(), 1), "wall": round(plan.wall(), 1), "text": estimate_line(plan)},
        "runs": [{"slot": r.slot, "kind": r.kind, "name": run_name(r), "timings": r.timings, "templates": r.templates,
                  "minutes": round(r.minutes, 1), "args": r.args()} for r in plan.runs],
        "unit": {"always": True, "command": "dotnet test tests/Gitberth.Tests"},
    }


def tsv_plan(plan):
    loads = plan.loads()
    lanes = " + ".join(f"{loads[k]:.1f}" for k in sorted(loads)) or "0"
    out = [f"# tier {plan.headline()}: {plan.summary()}; estimate about {plan.wall():.1f} min in all, lanes {lanes} min",
           f"# lanes {plan.lanes}"]
    for r in plan.runs:
        args = r.args()
        if any(re.search(r"\s", a) for a in args):
            raise MapError(f"an argument with a space: {args}")
        out.append("\t".join([str(r.slot), r.timings, ",".join(r.templates) or "-", " ".join(args)]))
    return "\n".join(out) + "\n"


def matrix_plan(plan):
    include = []
    for r in plan.runs:
        prefix = "t" if r.template_run else "s"
        include.append({"id": f"{prefix}{r.slot}", "name": run_name(r), "args": " ".join(r.args()), "timings": r.timings,
                        "templates": ",".join(r.templates)})
    return json.dumps({"include": include}, separators=(",", ":")) + "\n"


def read_change(args, head):
    """The changed files, and what they're compared with."""
    if args.git:
        changes, mb = changes_from_git(head, args.git)
        return changes, f"{args.git} (merge base {mb[:12]})"
    if args.trees:
        return changes_from_trees(args.trees[0], args.trees[1]), args.trees[0]
    if args.diff:
        return changes_from_diff(read_input(args.diff), head), "the diff " + ("on stdin" if args.diff == "-" else args.diff)
    return changes_from_list(read_input(args.changed), head), "the list " + ("on stdin" if args.changed == "-" else args.changed)


def make_plan(args):
    tier = args.tier
    if args.lanes is not None and args.shards is not None:
        raise Refused("--lanes and --shards don't go together")
    if args.template_shards is not None and args.shards is None:
        raise Refused("--template-shards goes with --shards")
    lanes = args.lanes if args.lanes is not None else (0 if args.shards else 1)
    shards = args.shards or 0
    if not 0 <= lanes <= 3 or (not shards and lanes < 1):
        raise Refused("--lanes is 1, 2 or 3")
    if shards < 0 or shards > 64 or (args.template_shards is not None and not 1 <= args.template_shards <= 16):
        raise Refused("--shards is 1-64 and --template-shards 1-16")
    if args.timings not in ("test", "production"):
        raise Refused("--timings is test or production")
    sources = [x for x in (args.git, args.trees, args.diff, args.changed) if x]
    if len(sources) > 1:
        raise Refused("give one of --git, --trees, --diff and --changed")
    if args.budget is not None and not 0 <= args.budget <= 600:
        raise Refused("--budget is minutes: 0 (no limit) to 600")
    head = Path(args.trees[1]) if args.trees else Path(args.root) if args.root else REPO
    for d in (args.trees or []) + ([args.root] if args.root else []):
        if not Path(d).is_dir():
            raise Refused(f"no such folder: {d}")
    imap = ImpactMap.load(head)
    index = scenario_index(head)
    catalog = sorted(p.stem for p in (head / CATALOG).glob("*.yaml"))
    planner = Planner(head, imap, index, catalog)

    tokens = []
    for t in re.split(r"[\s,]+", args.groups or ""):
        if not t:
            continue
        if not (GROUP_TOKEN.match(t) and t[:2] in imap.groups) and not (t.startswith("@") and t in imap.aliases):
            raise Refused(f"--groups takes group numbers (02, 12+slow) and aliases from {MAP}: {t!r}")
        tokens.append(t)
    templates = None
    if args.templates:
        templates = [t for t in args.templates.split(",") if t]
        bad = [t for t in templates if not TEMPLATE_ID.match(t) or t not in catalog]
        if bad:
            raise Refused(f"--templates takes the catalog's template ids ({', '.join(catalog)}): {', '.join(bad)}")
    for n in args.note or []:
        planner.note(n)

    sel = Selection()
    reasons = []
    base = ""
    change_tier = None
    if tier == "impact" and not sources:
        raise Refused("impact compares with a base: give --git BASE, --trees BASE_DIR HEAD_DIR, --diff or --changed")
    if tier == "groups" and not tokens:
        raise Refused("--tier groups needs --groups")
    if sources and tier in ("impact", "wide"):
        change_tier = "none"
        try:
            changes, base = read_change(args, head)
        except BaseUnknown as e:
            planner.note(f"{e}, so every group runs (full)")
            changes, base, change_tier = [], args.git, "full"
        for c in changes:
            for r in planner.file_reasons(c, args.no_refine):
                reasons.append(r)
                sub = Selection()
                planner.expand(sorted(r.tokens), sub, r.templates)
                if not (sub.groups or sub.nodes or sub.templates or sub.new_files):
                    r.tokens = set()
                    continue
                planner.expand(sorted(r.tokens), sel, r.templates)
                for n in r.notes:
                    planner.note(n)
                t = "scenarios" if r.scenario else "impact"
                if sub.aliases & {"@full", "@release"}:
                    t = "full"
                elif "@wide" in sub.aliases:
                    t = "wide"
                change_tier = max(change_tier, t, key=STRENGTH.index)
    if change_tier == "full":
        sel = Selection()   # every group but the template smoke
        planner.expand(["@full"], sel)
    if tier == "impact":
        final = change_tier
    elif tier == "wide":
        planner.expand(["@wide"], sel)
        final = max("wide", change_tier or "wide", key=STRENGTH.index)
    else:
        final = tier
        alias = {"smoke": ["@smoke"], "full": ["@full"], "release": ["@release"], "groups": []}[tier]
        planner.expand(alias, sel, ALL)
    planner.expand(tokens, sel, templates or ALL)
    if templates is not None and sel.templates is not None:
        sel.templates = set(templates)
    # The smoke: for a change outside the scenario files (Gitberth or the rig changed), unless asked otherwise.
    outside = any(r.tokens and not r.scenario for r in reasons)
    if args.smoke or (final in ("impact", "wide") and outside and not args.no_smoke):
        planner.expand([a for a in ("@core", "@smoke") if a in imap.aliases], sel)
    smoke = "@smoke" in sel.aliases
    planner.normalize(sel)
    if tier == "impact" and not (sel.groups or sel.nodes or sel.templates or sel.new_files):
        final = "none"

    builder = Builder(imap, index, catalog, args.timings)
    # The default tier has a wall-time budget: the unit tests beside the rigs' setup, then the lanes.
    budget = (args.budget if args.budget is not None else imap.budget) if tier == "impact" and not shards else 0.0
    left, cut = [], []
    if budget > 0:
        ranks = {}
        for r in reasons:
            for tok in sorted(r.tokens):
                sub = Selection()
                planner.expand([tok], sub, r.templates)
                keys = [("group", g) for g in sub.groups] + [("node", n) for n in sub.nodes] + \
                    [("file", f) for f in sub.new_files] + ([("templates", "")] if sub.templates is not None else [])
                for key in keys:
                    ranks[key] = min(ranks.get(key, r.rank(tok)), r.rank(tok))
        for key in sel.groups:   # what no change named (--groups, or every group when the base is unknown): after
            ranks.setdefault(("group", key), (3, 0))
        order = sorted(ranks, key=lambda k: (ranks[k], group_order(k[1][:2]) if k[0] == "group" else (99, k[1])))
        core = list(imap.aliases.get("@core", [])) if smoke else []
        rest = [n for n in imap.aliases.get("@smoke", []) if n not in core] if smoke else []
        runs, cut = builder.budget_runs(sel, order, core, rest, lanes, budget - imap.setup)
        left = left_out_text(cut, builder, sel, set(core) | set(rest))
        if left and final == "full":
            planner.note("This change reaches every group (a package or target change): run the full suite before it "
                         "goes out (-Tier full, or Actions > E2E).")
    else:
        runs = builder.runs(sel, lanes, shards, args.template_shards or 1, args.timings)
    return Plan(imap, final, tier, reasons, sel, smoke, runs, lanes, shards, planner.warnings, planner.notes,
                args.timings, base, budget, left, len(cut) if budget > 0 else 0)


def cmd_plan(args):
    plan = make_plan(args)
    fmt = {"text": text_report, "markdown": markdown_report, "json": lambda p: json.dumps(json_plan(p), indent=2) + "\n",
           "tsv": tsv_plan, "matrix": matrix_plan}[args.format]
    body = fmt(plan)
    if args.out:
        write(args.out, body)
    else:
        sys.stdout.write(body)
    if args.report:
        write(args.report, text_report(plan))
    return 0


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


# ---------------------------------------------------------------- check

def node_exists(index, node):
    parts = node.split("::")
    mod = index.get(parts[0][:-3]) if parts[0].endswith(".py") else None
    if mod is None or not mod.ok:
        return False
    if len(parts) == 2:
        return parts[1] in mod.classes or any(c is None and f == parts[1] for c, f, _, _ in mod.tests)
    if len(parts) == 3:
        return parts[2] in mod.classes.get(parts[1], [])
    return False


def cmd_check(args):
    root = Path(args.root) if args.root else REPO
    errors, warnings = [], []
    try:
        imap = ImpactMap.load(root, errors)
    except MapError as e:
        print(f"error: {e}")
        return 1
    index = scenario_index(root)
    folder = root / SCENARIOS
    for key, g in sorted(imap.groups.items()):
        if not (folder / g.file).is_file():
            errors.append(f"group {key}: no file {SCENARIOS}/{g.file}")
    for mod, m in sorted(index.items()):
        if not m.ok:
            errors.append(f"{mod}.py {m.error}")
        if mod + ".py" not in imap.by_file:
            errors.append(f"{mod}.py has no group in {MAP}")
    if len([g for g in imap.groups.values() if g.template_smoke]) != 1:
        errors.append("exactly one group must be the template smoke (template_smoke = true)")

    def valid(tok, in_rule):
        if tok.startswith("@"):
            return tok in imap.aliases or (in_rule and tok == SELF)
        if "::" in tok:
            return node_exists(index, tok)
        return bool(GROUP_TOKEN.match(tok)) and tok[:2] in imap.groups

    for name, items in imap.aliases.items():
        for tok in items:
            if not valid(tok, False):
                errors.append(f"alias {name}: {tok} " + ("doesn't exist" if "::" in tok else "isn't a group, an alias or a test"))
    planner = Planner(root, imap, index, [])
    for name in imap.aliases:
        try:
            planner.expand([name], Selection())
        except MapError as e:
            errors.append(f"alias {name}: {e}")
    for r in imap.rules:
        for tok in r.groups + [t for e in r.refine for t in e.groups]:
            if not valid(tok, True):
                errors.append(f"rule {r.index} ({r.name}): {tok} isn't a group, an alias or a test")
    for table, nodes in (("minutes", imap.minutes), ("test_minutes", imap.test_minutes)):
        for node in nodes:
            if not node_exists(index, node):
                errors.append(f"[{table}]: {node} doesn't exist")
    for key, g in imap.groups.items():
        for t in g.test_slow:
            if t not in g.slow:
                errors.append(f"group {key}: test_slow names {t}, which isn't one of its slow tests")
    for key, g in sorted(imap.groups.items()):
        m = index.get(g.module)
        if m is None or not m.ok or g.template_smoke:
            continue
        marked = {f for c, f, slow, _ in m.tests if slow and c is None} | {c for c, f, slow, _ in m.tests if slow and c}
        if marked != set(g.slow):
            errors.append(f"group {key}: slow lists {sorted(g.slow)}, but {g.file} marks {sorted(marked)} with @pytest.mark.slow")
    catalog = sorted(p.stem for p in (root / CATALOG).glob("*.yaml"))
    for t in imap.weights:
        if t not in catalog:
            warnings.append(f"[templates] weight: no template {t} in {CATALOG}")

    files = sorted(walk_tree(root))
    for r in imap.rules:
        if r.catch_all:
            continue
        for g, rx in zip(r.globs, r.matchers):
            if not any(rx.match(p) for p in files):
                warnings.append(f"rule {r.index}: {g} matches no file")
    caught = [p for p in files if (rule := imap.rule_for(p)) is None or rule.catch_all]
    if caught:
        warnings.append(f"{len(caught)} file(s) reach only the catch-all: " + ", ".join(caught[:20]) + (" ..." if len(caught) > 20 else ""))
    names = set()
    for mod, m in index.items():
        names.add(mod)
        names.update(m.classes)
        names.update(f for _, f, _, _ in m.tests)
    singles = sorted({t.split("::")[-1] for items in imap.aliases.values() for t in items if "::" in t})
    for s in singles:
        clash = sorted(n for n in names if s.lower() in n.lower() and n != s)
        if clash:
            warnings.append(f"{s} is part of other names too ({', '.join(clash[:4])}): select it by node ID, never with -k")
    for e in errors:
        print(f"error: {e}")
    for w in warnings:
        print(f"warning: {w}")
    tests = sum(c for m in index.values() if m.ok for _, _, _, c in m.tests)
    print(f"{MAP}: {len(imap.groups)} groups, {len(imap.aliases)} aliases, {len(imap.rules)} rules; {len(index)} scenario files, "
          f"{tests} tests; {len(errors)} error(s), {len(warnings)} warning(s)")
    return 1 if errors else 0


# ---------------------------------------------------------------- report and minutes (junit.xml)

def junit_files(dirs):
    out = []
    for d in dirs:
        d = Path(d)
        if d.is_file():
            out.append((d.parent.name, d))
            continue
        for p in sorted(d.rglob("junit.xml")):
            rel = p.parent.relative_to(d).as_posix()
            out.append((rel if rel != "." else d.name, p))
    return out


def junit_cases(path):
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError):
        return None
    cases = []
    for tc in root.iter("testcase"):
        outcome, message = "passed", ""
        for child in tc:
            if child.tag in ("failure", "error"):
                outcome = "failed" if child.tag == "failure" else "error"
                message = (child.get("message") or child.text or "").strip()
                break
            if child.tag == "skipped":
                outcome = "skipped"
        cls = tc.get("classname") or ""
        cases.append({"module": cls.split(".")[0], "class": ".".join(cls.split(".")[1:]), "name": tc.get("name") or "",
                      "time": float(tc.get("time") or 0), "outcome": outcome, "message": message})
    return cases


def cmd_report(args):
    rows, failures = [], []
    for name, path in junit_files(args.dirs):
        cases = junit_cases(path)
        if cases is None:
            rows.append((name, None))
            continue
        counts = {k: sum(1 for c in cases if c["outcome"] == k) for k in ("passed", "failed", "error", "skipped")}
        minutes = sum(c["time"] for c in cases) / 60
        rows.append((name, (counts, minutes)))
        for c in cases:
            if c["outcome"] in ("failed", "error"):
                node = f"{c['module']}.py::" + (f"{c['class']}::" if c["class"] else "") + c["name"]
                failures.append((name, node, c["message"].splitlines()[0][:300] if c["message"] else ""))
        rep = path.parent / "report.txt"
        if rep.is_file() and "UNROUTED" in rep.read_text(encoding="utf-8", errors="replace"):
            failures.append((name, "(the stand-ins)", "a call found no stand-in route, which fails the run (report.txt)"))
    out = ["### E2E results", ""]
    if args.unit:
        out.append(f"Unit tests: **{args.unit}**")
        out.append("")
    if not rows:
        out.append("No junit.xml found (did the end-to-end jobs run?).")
    else:
        out += ["| Run | Passed | Failed | Errors | Skipped | Minutes |", "|---|---:|---:|---:|---:|---:|"]
        total = {"passed": 0, "failed": 0, "error": 0, "skipped": 0}
        total_min = 0.0
        for name, data in rows:
            if data is None:
                out.append(f"| {name} | junit.xml doesn't parse | | | | |")
                continue
            counts, minutes = data
            for k in total:
                total[k] += counts[k]
            total_min += minutes
            out.append(f"| {name} | {counts['passed']} | {counts['failed']} | {counts['error']} | {counts['skipped']} | {minutes:.1f} |")
        out.append(f"| **all** | **{total['passed']}** | **{total['failed']}** | **{total['error']}** | {total['skipped']} | {total_min:.1f} |")
    if failures:
        out += ["", "#### Failures", ""]
        out += [f"- `{node}` ({name}): {msg}" for name, node, msg in failures]
    sys.stdout.write("\n".join(out) + "\n")
    return 0


def cmd_minutes(args):
    """Each group's measured minutes next to the map's. A run with test timings (the default) refreshes test_minutes,
    test_slow and [test_minutes]; one with production timings refreshes minutes, slow and [minutes]."""
    imap = ImpactMap.load(REPO)
    test = args.timings == "test"
    measured, slow, singles = {}, {}, {}
    for name, path in junit_files(args.dirs):
        per = {}
        for c in junit_cases(path) or []:
            g = imap.by_file.get(c["module"] + ".py")
            if g is None:
                continue
            if c["name"] in g.slow:
                slow.setdefault((g.key, c["name"]), []).append(c["time"] / 60)
            else:
                per[g.key] = per.get(g.key, 0.0) + c["time"] / 60
            node = f"{c['module']}.py::" + (f"{c['class']}::" if c["class"] else "") + c["name"]
            if node in imap.minutes or node in imap.test_minutes:
                singles.setdefault(node, []).append(c["time"] / 60)
        for key, minutes in per.items():
            measured.setdefault(key, []).append(minutes)
    if not measured:
        print("No junit.xml with scenario groups found.")
        return 1
    builder = Builder(imap, scenario_index(REPO), [], args.timings)
    print(f"group  map ({args.timings} timings)  measured (runs)")
    for key in sorted(imap.groups, key=group_order):
        g = imap.groups[key]
        got = measured.get(key)
        line = f"{key}     {builder.group_minutes(g):6.2f}          " + (f"{sum(got) / len(got):6.2f} ({len(got)})" if got else "     -")
        for t in sorted(g.slow):
            s = slow.get((key, t))
            line += f"   slow {t[:40]}: map {builder.slow_minutes(g, t):.2f}" + (f", measured {sum(s) / len(s):.2f}" if s else "")
        print(line)
    for node, values in sorted(singles.items()):
        print(f"single test {node}: measured {sum(values) / len(values):.2f}")
    field_name = "test_minutes" if test else "minutes"
    print(f"\nFor impact.toml ({field_name} in [groups]; test_slow or slow for the slow tests; "
          f"[{'test_minutes' if test else 'minutes'}] for the single tests):")
    for key in sorted(imap.groups, key=group_order):
        got = measured.get(key)
        if got:
            print(f'"{key}" {field_name} = {round(sum(got) / len(got), 2):g}')
    for (key, t), s in sorted(slow.items()):
        print(f'"{key}" {"test_slow" if test else "slow"} = {{ {t} = {round(sum(s) / len(s), 2):g} }}')
    for node, values in sorted(singles.items()):
        print(f'"{node}" = {round(sum(values) / len(values), 2):g}')
    return 0


# ---------------------------------------------------------------- main

def main(argv=None):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="replace")
    p = argparse.ArgumentParser(prog="impact.py", description=__doc__.split("\n\n")[0],
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    pl = sub.add_parser("plan", help="what a change runs, and why")
    pl.add_argument("--tier", choices=TIERS, default="impact")
    pl.add_argument("--git", metavar="BASE")
    pl.add_argument("--trees", nargs=2, metavar=("BASE_DIR", "HEAD_DIR"))
    pl.add_argument("--diff", metavar="FILE")
    pl.add_argument("--changed", metavar="FILE")
    pl.add_argument("--groups", default="")
    pl.add_argument("--templates", default="")
    smoke = pl.add_mutually_exclusive_group()
    smoke.add_argument("--smoke", action="store_true")
    smoke.add_argument("--no-smoke", action="store_true")
    pl.add_argument("--no-refine", action="store_true")
    pl.add_argument("--lanes", type=int)
    pl.add_argument("--shards", type=int)
    pl.add_argument("--template-shards", type=int)
    pl.add_argument("--timings", default="test")
    pl.add_argument("--format", choices=("text", "markdown", "json", "tsv", "matrix"), default="text")
    pl.add_argument("--out")
    pl.add_argument("--report")
    pl.add_argument("--note", action="append")
    pl.add_argument("--root", help="the head tree when there's no --trees (default: this file's repo)")
    pl.add_argument("--budget", type=float, help="impact only: minutes of wall time in all (default: the map's; 0: no limit)")
    ck = sub.add_parser("check", help="the map against the tree")
    ck.add_argument("--root")
    rp = sub.add_parser("report", help="junit.xml files -> a Markdown table")
    rp.add_argument("dirs", nargs="+")
    rp.add_argument("--unit", default="")
    mn = sub.add_parser("minutes", help="measured minutes next to the map's")
    mn.add_argument("dirs", nargs="+")
    mn.add_argument("--timings", choices=("test", "production"), default="test",
                    help="the timings the runs had: test (the default) refreshes test_minutes, production minutes")
    try:
        args = p.parse_args(argv)
    except SystemExit as e:
        return int(e.code or 0)
    try:
        return {"plan": cmd_plan, "check": cmd_check, "report": cmd_report, "minutes": cmd_minutes}[args.command](args)
    except Refused as e:
        print(f"impact.py: {e}", file=sys.stderr)
        return 2
    except MapError as e:
        print(f"impact.py: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
