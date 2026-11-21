"""The selector's own tests (tests/e2e/impact.py). They take seconds and need only Python (the git case also needs git):

    python3 -m unittest discover -s tests/e2e -p impact_test.py

Most build a small repo in a temporary folder: a map, a few scenario files and a few product files. The last ones use
the real map and scenarios."""
import contextlib
import io
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import impact  # noqa: E402

FAKE_MAP = """
version = 1

[budget]
setup = 3.5

[groups]
"01" = { file = "test_01_alpha.py", minutes = 2 }
"02" = { file = "test_02_beta.py", minutes = 4 }
"03" = { file = "test_03_gamma.py", minutes = 3 }
"06" = { file = "test_06_copy.py", minutes = 1 }
"12" = { file = "test_12_statuses.py", minutes = 7, slow = { test_a_scheduled_restart = 6.5 } }
"19" = { file = "test_19_volumes.py", minutes = 6 }
"20" = { file = "test_20_smoke.py", minutes = 2.5 }
"28" = { file = "test_28_compose.py", minutes = 10 }
"30" = { file = "test_30_templates.py", minutes = 3.5 }
"31" = { file = "test_31_template_smoke.py", minutes = 20, template_smoke = true }
"32" = { file = "test_32_agent.py", minutes = 1.25 }

[aliases]
"@core" = ["test_20_smoke.py::test_sign_in_and_out"]
"@smoke" = ["test_02_beta.py::TestOpened::test_preview", "test_20_smoke.py::test_sign_in_and_out"]
"@wide" = ["@smoke", "01", "12", "20"]
"@full" = ["01", "02", "03", "06", "12+slow", "19", "20", "28", "30", "32"]
"@release" = ["@full", "31"]
"@none" = []
"@t12-sched" = ["test_12_statuses.py::test_a_scheduled_restart"]
"@t30-gallery" = ["test_30_templates.py::test_gallery"]

[minutes]
"test_20_smoke.py::test_sign_in_and_out" = 0.2

[templates]
weight = { nextcloud = 3 }

[[rule]]
globs = ["docs/**", "README.md", "tests/e2e/impact.toml"]
groups = ["@none"]

[[rule]]
globs = ["tests/e2e/scenarios/test_*.py"]
groups = ["@self+importers"]

[[rule]]
globs = ["src/Gitberth/Gitberth.csproj"]
groups = ["@smoke"]
refine = [
    { match = 'PackageReference|TargetFramework|Sdk', groups = ["@full"] },
]

[[rule]]
globs = ["src/Gitberth/Deploy/DeployPipeline.Compose.cs"]
groups = ["28", "30", "32"]

[[rule]]
globs = ["src/Gitberth/Deploy/DeployPipeline.cs"]
groups = ["@wide"]
refine = [
    { match = 'Compose', groups = ["28", "32"] },
    { match = 'DeclaredVolume|[Tt]mpfs', groups = ["06", "19"] },
]

[[rule]]
globs = ["src/Gitberth/Templates/catalog/*.svg"]
groups = ["@t30-gallery"]

[[rule]]
globs = ["src/Gitberth/Templates/catalog/miniflux.*"]
groups = ["30", "31", "32"]

[[rule]]
globs = ["src/Gitberth/Services/RestartScheduler.cs"]
groups = ["@t12-sched"]
notes = ["Look at the scheduler's unit tests too."]

[[rule]]
globs = ["src/Gitberth/Previews/**"]
groups = ["01", "02"]

[[rule]]
globs = ["src/Gitberth/Domains/**"]
groups = ["03"]

[[rule]]
globs = ["**"]
groups = ["@wide"]
catch_all = true
"""

PIPELINE = """namespace Gitberth.Deploy;

public sealed class DeployPipeline
{
    private int _count;

    public async Task<int> ApplyDeclaredVolumesAsync(string app)
    {
        var a = 1;
        var b = 2;
        var c = 3;
        var d = 4;
        var e = 5;
        await Task.Yield();
        return a + b + c + d + e;
    }

    public int Count()
    {
        var x = _count;
        var y = 2;
        var z = 3;
        var w = 4;
        return x + y + z + w;
    }
}
"""

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="8.0.0" />
  </ItemGroup>
</Project>
"""

BETA = """import pytest

FORKER = "forker"


def new_app(name):
    return name.upper()


def untouched():
    return 1


@pytest.fixture
def own_apps():
    return [new_app("x")]


class TestOpened:
    def test_preview(self, own_apps):
        assert own_apps

    def test_tab(self):
        assert True


def test_other():
    assert untouched() == 1
"""

STATUSES = """import pytest


def make_app():
    return "app"


def test_one():
    assert make_app()


def test_two():
    assert True


@pytest.mark.slow
def test_a_scheduled_restart():
    assert make_app()
"""

FILES = {
    "README.md": "# Readme\n\nLine two.\n",
    "docs/guide.md": "# Guide\n\nHow it works.\n",
    "tests/e2e/impact.toml": FAKE_MAP,
    "src/Gitberth/Gitberth.csproj": CSPROJ,
    "src/Gitberth/Deploy/DeployPipeline.cs": PIPELINE,
    "src/Gitberth/Deploy/DeployPipeline.Compose.cs": "namespace Gitberth.Deploy;\n\npublic partial class DeployPipeline { }\n",
    "src/Gitberth/Previews/PreviewThing.cs": "namespace Gitberth.Previews;\n\npublic sealed class PreviewThing { }\n",
    "src/Gitberth/Services/RestartScheduler.cs": "namespace Gitberth.Services;\n\npublic sealed class RestartScheduler { }\n",
    "src/Gitberth/Templates/catalog/miniflux.yaml": "id: miniflux\nname: Miniflux\n",
    "src/Gitberth/Templates/catalog/miniflux.svg": "<svg/>\n",
    "src/Gitberth/Templates/catalog/memos.yaml": "id: memos\nname: Memos\n",
    "src/Gitberth/Templates/catalog/nextcloud.yaml": "id: nextcloud\nname: Nextcloud\n",
    "src/Gitberth/Templates/catalog/x.svg": "<svg/>\n",
    "tests/e2e/scenarios/test_01_alpha.py": "def helper():\n    return 1\n\n\ndef test_alpha():\n    assert helper() == 1\n",
    "tests/e2e/scenarios/test_02_beta.py": BETA,
    "tests/e2e/scenarios/test_03_gamma.py": "from test_02_beta import new_app\n\n\ndef test_gamma():\n    assert new_app('a') == 'A'\n",
    "tests/e2e/scenarios/test_06_copy.py": "from test_02_beta import own_apps  # noqa: F401\n\n\ndef test_copy(own_apps):\n    assert own_apps\n",
    "tests/e2e/scenarios/test_12_statuses.py": STATUSES,
    "tests/e2e/scenarios/test_19_volumes.py": "from test_02_beta import untouched\n\n\ndef test_volumes():\n    assert untouched() == 1\n",
    "tests/e2e/scenarios/test_20_smoke.py": "def test_sign_in_and_out():\n    assert True\n\n\ndef test_new_app():\n    assert True\n",
    "tests/e2e/scenarios/test_28_compose.py": "def test_compose():\n    assert True\n",
    "tests/e2e/scenarios/test_30_templates.py": "def test_gallery():\n    assert True\n\n\ndef test_install():\n    assert True\n",
    "tests/e2e/scenarios/test_31_template_smoke.py": "import pytest\n\n\n@pytest.mark.slow\ndef test_31_template_smoke():\n    assert True\n",
    "tests/e2e/scenarios/test_32_agent.py": "def test_agent():\n    assert True\n",
}

SMOKE = {"test_02_beta.py::TestOpened::test_preview", "test_20_smoke.py::test_sign_in_and_out"}


def run(*argv):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = impact.main([str(a) for a in argv])
    return code, out.getvalue(), err.getvalue()


def write(root, files):
    for rel, text in files.items():
        p = Path(root) / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        if text is None:
            p.unlink(missing_ok=True)
        else:
            p.write_bytes(text.encode("utf-8"))


class FakeRepo(unittest.TestCase):
    """A base tree and a head tree, the head changed by each test."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="impact-test-"))
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.base = self.tmp / "base"
        self.head = self.tmp / "head"
        write(self.base, FILES)
        write(self.head, FILES)

    def change(self, rel, old, new):
        p = self.head / rel
        text = p.read_text(encoding="utf-8")
        self.assertIn(old, text)
        p.write_text(text.replace(old, new, 1), encoding="utf-8", newline="")

    def plan(self, *extra, tier="impact"):
        code, out, err = run("plan", "--tier", tier, "--trees", self.base, self.head, "--format", "json", *extra)
        self.assertEqual(code, 0, err)
        return json.loads(out)

    def groups_plan(self, groups, *extra):
        code, out, err = run("plan", "--tier", "groups", "--root", self.head, "--groups", groups, "--format", "json", *extra)
        self.assertEqual(code, 0, err)
        return json.loads(out)


class TestChoosing(FakeRepo):
    def test_01_docs_only_runs_nothing_in_the_rig(self):
        self.change("docs/guide.md", "How it works.", "How it really works.")
        self.change("README.md", "Line two.", "Line 2.")
        p = self.plan()
        self.assertEqual(p["tier"], "none")
        self.assertEqual(p["runs"], [])
        self.assertTrue(p["unit"]["always"])
        self.assertEqual({r["path"] for r in p["reasons"]}, {"docs/guide.md", "README.md"})

    def test_02_a_changed_helper_runs_its_group_and_only_the_modules_that_import_it(self):
        self.change("tests/e2e/scenarios/test_02_beta.py", "def untouched():\n    return 1", "def untouched():\n    return 2")
        p = self.plan()
        self.assertEqual(p["tier"], "scenarios")
        self.assertEqual(set(p["selection"]["groups"]), {"02", "19"})
        self.assertFalse(p["selection"]["smoke"])
        self.assertEqual(p["selection"]["tests"], [])

    def test_03_a_fixture_that_calls_a_changed_helper_takes_its_importers_along(self):
        self.change("tests/e2e/scenarios/test_02_beta.py", "return name.upper()", "return name.title()")
        p = self.plan()
        # new_app changed; own_apps calls it, so test_06 (which imports own_apps) runs too; untouched's test_19 doesn't
        self.assertEqual(set(p["selection"]["groups"]), {"02", "03", "06"})
        self.assertEqual(p["tier"], "scenarios")

    def test_04_a_word_in_the_enclosing_declaration_narrows_a_hub(self):
        self.change("src/Gitberth/Deploy/DeployPipeline.cs", "return a + b + c + d + e;", "return a + b + c + d + e + 1;")
        p = self.plan()
        self.assertEqual(p["tier"], "impact")
        self.assertEqual(set(p["selection"]["groups"]), {"06", "19"})
        self.assertTrue(p["selection"]["smoke"])
        self.assertEqual(set(p["selection"]["tests"]), SMOKE)
        self.assertIn("DeclaredVolume", p["reasons"][0]["how"])

    def test_05_a_hub_without_a_narrowing_word_runs_wide(self):
        self.change("src/Gitberth/Deploy/DeployPipeline.cs", "return x + y + z + w;", "return x + y + z + w + 1;")
        p = self.plan()
        self.assertEqual(p["tier"], "wide")
        self.assertEqual(set(p["selection"]["groups"]), {"01", "12", "20"})
        self.assertIn("no hunk word", p["reasons"][0]["how"])

    def test_06_a_package_change_runs_everything(self):
        self.change("src/Gitberth/Gitberth.csproj", 'Version="8.0.0"', 'Version="8.1.0"')
        p = self.plan()
        self.assertEqual(p["tier"], "full")
        self.assertEqual(set(p["selection"]["groups"]), {"01", "02", "03", "06", "12", "19", "20", "28", "30", "32"})
        self.assertEqual(p["selection"]["groups"]["12"], ["test_a_scheduled_restart"])
        self.assertIsNone(p["selection"]["templates"])

    def test_07_a_catalog_template_runs_its_groups_and_a_template_smoke_of_its_own(self):
        self.change("src/Gitberth/Templates/catalog/miniflux.yaml", "name: Miniflux", "name: Miniflux 2")
        p = self.plan("--lanes", "2")
        self.assertEqual(p["tier"], "impact")
        self.assertEqual(set(p["selection"]["groups"]), {"30", "32"})
        self.assertTrue(p["selection"]["smoke"])
        smoke_runs = [r for r in p["runs"] if r["args"] == ["-k", "test_31_template_smoke"]]
        self.assertEqual(len(smoke_runs), 1)
        self.assertEqual(smoke_runs[0]["templates"], ["miniflux"])
        self.assertEqual(smoke_runs[0]["timings"], "production")
        self.assertEqual(smoke_runs[0]["slot"], 1)
        others = [r for r in p["runs"] if r is not smoke_runs[0]]
        self.assertTrue(all(r["timings"] == "test" for r in others))

    def test_08_an_icon_runs_the_gallery_test_and_no_template_smoke(self):
        self.change("src/Gitberth/Templates/catalog/x.svg", "<svg/>", "<svg></svg>")
        p = self.plan()
        self.assertIsNone(p["selection"]["templates"])
        self.assertNotIn("30", p["selection"]["groups"])
        self.assertIn("test_30_templates.py::test_gallery", p["selection"]["tests"])
        self.assertFalse(any("test_31_template_smoke" in r["args"] for r in p["runs"]))

    def test_09_a_rename_across_two_areas_takes_both_rules(self):
        old, new = "src/Gitberth/Previews/PreviewThing.cs", "src/Gitberth/Domains/PreviewThing.cs"
        write(self.head, {new: FILES[old], old: None})
        p = self.plan()
        self.assertEqual(set(p["selection"]["groups"]), {"01", "02", "03"})
        # the same as a git diff says it
        diff = (f"diff --git a/{old} b/{new}\nsimilarity index 100%\nrename from {old}\nrename to {new}\n")
        (self.tmp / "rename.diff").write_text(diff, encoding="utf-8")
        code, out, err = run("plan", "--diff", self.tmp / "rename.diff", "--root", self.head, "--format", "json")
        self.assertEqual(code, 0, err)
        self.assertEqual(set(json.loads(out)["selection"]["groups"]), {"01", "02", "03"})

    def test_10_trees_and_a_diff_give_the_same_plan_and_line_endings_are_no_change(self):
        self.change("src/Gitberth/Deploy/DeployPipeline.cs", "return a + b + c + d + e;", "return a + b + c + d + e + 1;")
        self.change("tests/e2e/scenarios/test_02_beta.py", "return name.upper()", "return name.title()")
        (self.head / "docs/guide.md").write_bytes(FILES["docs/guide.md"].replace("\n", "\r\n").encode())
        by_trees = self.plan("--lanes", "2")
        self.assertNotIn("docs/guide.md", [r["path"] for r in by_trees["reasons"]])
        diff = []
        for rel in ("src/Gitberth/Deploy/DeployPipeline.cs", "tests/e2e/scenarios/test_02_beta.py"):
            a = (self.base / rel).read_text(encoding="utf-8").splitlines()
            b = (self.head / rel).read_text(encoding="utf-8").splitlines()
            diff.append(f"diff --git a/{rel} b/{rel}")
            diff += list(__import__("difflib").unified_diff(a, b, f"a/{rel}", f"b/{rel}", n=3, lineterm=""))
        (self.tmp / "change.diff").write_text("\n".join(diff) + "\n", encoding="utf-8")
        code, out, err = run("plan", "--diff", self.tmp / "change.diff", "--root", self.head, "--lanes", "2", "--format", "json")
        self.assertEqual(code, 0, err)
        by_diff = json.loads(out)
        for key in ("tier", "selection", "runs"):
            self.assertEqual(by_trees[key], by_diff[key], key)
        self.assertEqual(set(by_trees["selection"]["groups"]), {"02", "03", "06", "19"})


class TestRuns(FakeRepo):
    def args(self, plan):
        return [a for r in plan["runs"] for a in r["args"]]

    def test_11_slow_tests_stay_out_unless_asked_for(self):
        slow = "test_12_statuses.py::test_a_scheduled_restart"
        self.assertEqual(self.args(self.groups_plan("12")), ["test_12_statuses.py", "--deselect", slow])
        self.assertEqual(self.args(self.groups_plan("12+slow")), ["test_12_statuses.py"])
        self.assertEqual(self.args(self.groups_plan("@t12-sched")), [slow])
        self.assertEqual(self.args(self.groups_plan("12 @t12-sched")), ["test_12_statuses.py"])
        # on two lanes the slow test is an item of its own, deselected from its group: it runs exactly once
        two = self.groups_plan("12+slow", "--lanes", "2")
        self.assertEqual(sorted(r["args"] for r in two["runs"]), [["test_12_statuses.py", "--deselect", slow], [slow]])

    def test_12_a_whole_group_takes_in_its_single_tests(self):
        p = self.groups_plan("20", "--smoke")
        self.assertIn("test_20_smoke.py", self.args(p))
        self.assertNotIn("test_20_smoke.py::test_sign_in_and_out", self.args(p))
        self.assertIn("test_02_beta.py::TestOpened::test_preview", self.args(p))

    def test_13_lanes_and_shards_are_deterministic_and_their_minutes_add_up(self):
        imap = impact.ImpactMap.load(self.head)
        whole = sum(g.minutes + sum(g.slow.values()) for k, g in imap.groups.items() if not g.template_smoke)
        for extra in (("--lanes", "2"), ("--lanes", "3"), ("--shards", "3", "--template-shards", "2")):
            first = self.plan(*extra, tier="release")
            again = self.plan(*extra, tier="release")
            self.assertEqual(first["runs"], again["runs"], extra)
            runs = first["runs"]
            smokes = sum(1 for r in runs if r["args"] == ["-k", "test_31_template_smoke"])
            # every template's share, plus the smoke's own setup once more for each extra template run
            expected = whole + 20 + impact.TEMPLATE_SETUP_MINUTES * (smokes - 1) + impact.RUN_MINUTES * len(runs)
            self.assertAlmostEqual(sum(r["minutes"] for r in runs), expected, delta=0.05 * len(runs), msg=extra)
            listed = [a for r in runs for a in r["args"] if a.endswith(".py")]
            self.assertEqual(len(listed), len(set(listed)), extra)    # each group in one run only
        shards = self.plan("--shards", "3", "--template-shards", "2", tier="release")
        tpl = [r for r in shards["runs"] if r["kind"] == "templates"]
        self.assertEqual(sorted(r["templates"] for r in tpl), [["memos", "miniflux"], ["nextcloud"]])

    def test_14_the_tsv_and_matrix_shapes(self):
        self.change("src/Gitberth/Templates/catalog/miniflux.yaml", "name: Miniflux", "name: Miniflux 2")
        code, out, err = run("plan", "--trees", self.base, self.head, "--lanes", "2", "--format", "tsv")
        self.assertEqual(code, 0, err)
        lines = out.splitlines()
        self.assertTrue(lines[0].startswith("# tier impact: "), lines[0])
        self.assertEqual(lines[1], "# lanes 2")
        rows = [line.split("\t") for line in lines[2:]]
        self.assertTrue(rows)
        for lane, timings, templates, args in rows:
            self.assertIn(lane, ("1", "2"))
            self.assertIn(timings, ("test", "production"))
            self.assertRegex(templates, r"^(-|[a-z0-9-]+(,[a-z0-9-]+)*)$")
            self.assertNotIn("  ", args)
            self.assertNotIn("\t", args)
        self.assertIn(["1", "production", "miniflux", "-k test_31_template_smoke"], rows)
        code, out, err = run("plan", "--trees", self.base, self.head, "--shards", "2", "--format", "matrix")
        self.assertEqual(code, 0, err)
        self.assertEqual(len(out.strip().splitlines()), 1)
        matrix = json.loads(out)
        self.assertTrue(matrix["include"])
        for job in matrix["include"]:
            self.assertEqual(set(job), {"id", "name", "args", "timings", "templates"})
            self.assertRegex(job["id"], r"^[st]\d+$")
        self.assertIn("miniflux", [j["templates"] for j in matrix["include"]])

    def test_15_groups_that_are_not_groups_are_refused(self):
        for bad in ("02; rm -rf /", "02 $(id)", "--deselect", "99", "@nope", "test_02_beta.py"):
            code, out, err = run("plan", "--tier", "groups", "--root", self.head, "--groups", bad)
            self.assertEqual(code, 2, bad)
            self.assertEqual(out, "")
        code, _, _ = run("plan", "--tier", "groups", "--root", self.head, "--groups", "31", "--templates", "memos;id")
        self.assertEqual(code, 2)
        code, _, _ = run("plan", "--tier", "groups", "--root", self.head, "--groups", "31", "--templates", "nosuch")
        self.assertEqual(code, 2)
        code, _, _ = run("plan", "--git", "--output=/tmp/x", "--root", self.head)
        self.assertEqual(code, 2)

    def test_notes_and_the_catch_all_are_reported(self):
        write(self.head, {"src/Gitberth/Services/RestartScheduler.cs": "namespace Gitberth.Services;\n// changed\n",
                          "src/Other/Thing.cs": "class Thing { }\n"})
        p = self.plan()
        self.assertIn("Look at the scheduler's unit tests too.", p["notes"])
        self.assertTrue(any("src/Other/Thing.cs" in w for w in p["warnings"]))
        self.assertEqual(p["tier"], "wide")
        self.assertEqual(p["selection"]["groups"]["12"], ["test_a_scheduled_restart"])

    def test_a_new_scenario_file_runs_as_itself(self):
        write(self.head, {"tests/e2e/scenarios/test_33_new.py": "from test_02_beta import untouched\n\n\ndef test_new():\n    pass\n"})
        p = self.plan()
        self.assertEqual(p["selection"]["new_files"], ["test_33_new.py"])
        self.assertTrue(any("test_33_new.py has no group" in w for w in p["warnings"]))
        self.assertIn(["test_33_new.py"], [r["args"] for r in p["runs"]])

    def test_template_groups_and_templates(self):
        p = self.groups_plan("31", "--templates", "memos", "--lanes", "2")
        self.assertEqual(p["runs"], [r for r in p["runs"] if r["args"] == ["-k", "test_31_template_smoke"]])
        self.assertEqual([r["templates"] for r in p["runs"]], [["memos"]])
        every = self.groups_plan("31")
        self.assertEqual([r["templates"] for r in every["runs"]], [[]])    # E2E_TEMPLATES empty: every template


class TestBudget(FakeRepo):
    """The default tier's wall-time budget: the unit tests and setup (3.5 min here), then the lanes."""

    def wide_change(self, *extra):
        self.change("src/Gitberth/Deploy/DeployPipeline.cs", "return x + y + z + w;", "return x + y + z + w + 1;")
        return self.plan("--lanes", "1", *extra)

    def args(self, plan):
        return [a for r in plan["runs"] for a in r["args"]]

    def test_a_selection_over_the_budget_is_cut_and_what_is_left_is_listed(self):
        p = self.wide_change("--budget", "10")
        self.assertLessEqual(p["estimate"]["wall"], 10)
        self.assertIn("test_01_alpha.py", self.args(p))                # a group that fits runs whole
        self.assertIn("test_12_statuses.py::test_one", self.args(p))   # one that doesn't, test by test
        self.assertNotIn("test_12_statuses.py::test_two", self.args(p))
        left = " ".join(p["left_out"])
        for name in ("test_two", "test_new_app", "TestOpened::test_preview"):
            self.assertIn(name, left)
        code, out, _ = run("plan", "--trees", self.base, self.head, "--lanes", "1", "--budget", "10")
        self.assertIn("cut to the 10-minute budget: 3 tests left for the full suite", out)
        self.assertIn("left for the full suite", out)

    def test_no_budget_keeps_everything(self):
        p = self.wide_change("--budget", "0")
        self.assertEqual(p["left_out"], [])
        args = self.args(p)
        for name in ("test_01_alpha.py", "test_12_statuses.py", "test_20_smoke.py", "test_02_beta.py::TestOpened::test_preview"):
            self.assertIn(name, args)

    def test_the_core_smoke_is_always_in(self):
        p = self.wide_change("--budget", "1")
        self.assertIn("test_20_smoke.py::test_sign_in_and_out", self.args(p))
        self.assertEqual(len([a for a in self.args(p) if a.endswith(".py") or "::" in a]), 1)
        self.assertTrue(p["left_out"])

    def test_most_specific_first(self):
        # A refine hit (06 19) comes before @wide's groups (01 12 20), which the same change's other hunk selects. With
        # 2.5 min in the lane: the core, then 06 (1 min); 19 (6 min) and 01 (2 min) don't fit.
        self.change("src/Gitberth/Deploy/DeployPipeline.cs", "return a + b + c + d + e;", "return a + b + c + d + e + 1;")
        p = self.wide_change("--budget", "6")
        args = self.args(p)
        self.assertIn("test_06_copy.py", args)
        self.assertNotIn("test_01_alpha.py", args)
        left = " ".join(p["left_out"])
        self.assertIn("19 (the whole group", left)
        self.assertIn("01 (the whole group", left)


class TestDeclarations(unittest.TestCase):
    def test_csharp(self):
        yes = ["    public async Task<int> ApplyAsync(string app)", "    internal static (bool Ok, string Why) Check(",
               "    public DeployPipeline(IOptions o) : base(o)", "public sealed class DeployPipeline",
               "    private sealed record Knob(string What);", "    static string Local(int x) => x.ToString();",
               "    [Fact] public void It_works()", "    Task<int> FooAsync(CancellationToken ct);"]
        no = ["        await Task.Delay(Tick, stoppingToken);", "        var every = Foo(minutes);", "        if (x is { } y)",
              "        return Build(app);", "        Foo(bar);", "        _logger.LogInformation(\"x\");",
              "        string json = JsonSerializer.Serialize(x);", "        } else if (y) {", "        throw new X(\"y\");"]
        for line in yes:
            self.assertTrue(impact.is_cs_declaration(line), line)
        for line in no:
            self.assertFalse(impact.is_cs_declaration(line), line)

    def test_razor_markup_and_code(self):
        lines = ['<section id="github">', "  <p>Token</p>", '  <input id="gh-token" />', "  <p>x</p>", "@code {",
                 "    private int count;", "    private void Save()", "    {", "        count++;", "    }", "}"]
        self.assertEqual(impact.enclosing("A.razor", lines, 3), ['  <input id="gh-token" />'])
        self.assertEqual(impact.enclosing("A.razor", lines, 8), ["    private void Save()"])
        self.assertEqual(impact.enclosing("A.razor", lines, 6), [])   # in @code, above any method: no markup id


@unittest.skipUnless(shutil.which("git"), "needs git")
class TestGit(FakeRepo):
    def git(self, *args):
        subprocess.run(["git", "-C", str(self.head), *args], check=True, capture_output=True)

    def test_the_working_tree_against_a_base_commit(self):
        self.git("init", "-q")
        self.git("-c", "user.name=t", "-c", "user.email=t@example.com", "add", "-A")
        self.git("-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "base")
        self.change("src/Gitberth/Deploy/DeployPipeline.cs", "return a + b + c + d + e;", "return a + b + c + d + e + 1;")
        write(self.head, {"docs/new.md": "untracked\n"})
        code, out, err = run("plan", "--git", "HEAD", "--root", self.head, "--format", "json")
        self.assertEqual(code, 0, err)
        p = json.loads(out)
        self.assertEqual(set(p["selection"]["groups"]), {"06", "19"})
        self.assertIn("docs/new.md", [r["path"] for r in p["reasons"]])
        code, out, err = run("plan", "--git", "no-such-branch", "--root", self.head, "--format", "json")
        self.assertEqual(code, 0, err)
        self.assertEqual(json.loads(out)["tier"], "full")


class TestReport(unittest.TestCase):
    def test_junit_files_make_one_table(self):
        tmp = Path(tempfile.mkdtemp(prefix="impact-report-"))
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        (tmp / "e2e-s1" / "a").mkdir(parents=True)
        (tmp / "e2e-s1" / "a" / "junit.xml").write_text(
            '<testsuites><testsuite name="pytest">'
            '<testcase classname="test_20_smoke" name="test_sign_in_and_out" time="12.0"/>'
            '<testcase classname="test_12_commit_statuses" name="test_one" time="60.0">'
            '<failure message="AssertionError: no status">trace</failure></testcase>'
            '</testsuite></testsuites>', encoding="utf-8")
        code, out, _ = run("report", tmp, "--unit", "success")
        self.assertEqual(code, 0)
        self.assertIn("| e2e-s1/a | 1 | 1 | 0 | 0 | 1.2 |", out)
        self.assertIn("`test_12_commit_statuses.py::test_one` (e2e-s1/a): AssertionError: no status", out)
        self.assertIn("Unit tests: **success**", out)

    def test_failures_become_annotations(self):
        tmp = Path(tempfile.mkdtemp(prefix="impact-annotate-"))
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        write(tmp, {
            # A run with a report: its FAILED blocks (assertion, screenshots, Gitberth's log) and its UNROUTED line.
            "out/a/report.txt": "SUMMARY: 1 passed, 1 failed in 60s; 1 call(s) found no stand-in route, which fails the run\n"
                                "UNROUTED (each got a bare 404 or Cloudflare's 7003; the stand-ins need these routes): GET api.github.com/x\n"
                                "PASSED: test_20_smoke.py::test_sign_in_and_out\n"
                                "\n"
                                "FAILED: test_36_disk.py::test_cleans_up (call)\n"
                                "  AssertionError: 50% full, a: b\n"
                                "  \n"
                                "  screenshots: http://x -> shot.png\n"
                                "  Gitberth's log during it:\n"
                                "    warn: Disk cleanup failed\n",
            "out/a/junit.xml": '<testsuites><testsuite><testcase classname="test_36_disk" name="test_cleans_up">'
                               '<failure message="junit only">trace</failure></testcase></testsuite></testsuites>',
            # A run with only junit.xml.
            "out/b/junit.xml": '<testsuites><testsuite><testcase classname="test_15_refusals" name="test_one">'
                               '<failure message="AssertionError: no banner">trace</failure></testcase></testsuite></testsuites>',
            # The Logs step's files are no run.
            "out/gitberth.log": "info: started\n",
        })
        code, out, _ = run("annotate", tmp / "out")
        self.assertEqual(code, 0)
        lines = out.splitlines()
        self.assertEqual(lines, [
            "::error title=E2E a%3A calls no stand-in route answered::UNROUTED (each got a bare 404 or Cloudflare's 7003; "
            "the stand-ins need these routes): GET api.github.com/x",
            "::error title=E2E a%3A test_36_disk.py%3A%3Atest_cleans_up (call)::AssertionError: 50%25 full, a: b%0A%0A"
            "screenshots: http://x -> shot.png%0AGitberth's log during it:%0A  warn: Disk cleanup failed",
            "::error title=E2E b%3A test_15_refusals.py%3A%3Atest_one::AssertionError: no banner",
            "3 failure(s) annotated",
        ])

        # No run at all (the rig didn't come up): said, not silent.
        (tmp / "empty").mkdir()
        code, out, _ = run("annotate", tmp / "empty")
        self.assertEqual(code, 0)
        self.assertTrue(out.startswith("::error title=E2E empty%3A no results::"), out)

        # Past ten, the last annotation names the rest.
        many = {f"many/{i:02}/junit.xml": f'<testsuites><testsuite><testcase classname="test_01_a" name="t{i}">'
                                          f'<failure message="m{i}">x</failure></testcase></testsuite></testsuites>'
                for i in range(12)}
        write(tmp, many)
        code, out, _ = run("annotate", tmp / "many")
        errors = [line for line in out.splitlines() if line.startswith("::error")]
        self.assertEqual(len(errors), 10)
        self.assertTrue(errors[-1].startswith("::error title=E2E%3A 3 more failures::09: test_01_a.py::t9%0A10: "), errors[-1])


class TestRealMap(unittest.TestCase):
    def test_16_the_map_matches_the_tree(self):
        code, out, _ = run("check")
        self.assertEqual(code, 0, out)

    def test_17_the_compose_half_of_the_pipeline(self):
        tmp = Path(tempfile.mkdtemp(prefix="impact-real-"))
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        (tmp / "changed.txt").write_text("src/Gitberth/Deploy/DeployPipeline.Compose.cs\n", encoding="utf-8")
        code, out, err = run("plan", "--changed", tmp / "changed.txt", "--format", "json")
        self.assertEqual(code, 0, err)
        p = json.loads(out)
        self.assertEqual(p["tier"], "impact")
        self.assertEqual(set(p["selection"]["groups"]), {"28", "30", "32"})
        self.assertTrue(p["selection"]["smoke"])

    def test_smoke_and_release_on_the_real_map(self):
        code, out, err = run("plan", "--tier", "smoke", "--lanes", "2", "--format", "json")
        self.assertEqual(code, 0, err)
        p = json.loads(out)
        self.assertEqual(len(p["selection"]["tests"]), 6)
        self.assertEqual(len(p["runs"]), 2)
        code, out, err = run("plan", "--tier", "release", "--shards", "8", "--template-shards", "3", "--format", "matrix")
        self.assertEqual(code, 0, err)
        jobs = json.loads(out)["include"]
        self.assertEqual(len([j for j in jobs if j["id"].startswith("s")]), 8)
        self.assertEqual(len([j for j in jobs if j["id"].startswith("t")]), 3)
        self.assertEqual(sum(len(j["templates"].split(",")) for j in jobs if j["templates"]),
                         len(list((impact.REPO / impact.CATALOG).glob("*.yaml"))))


if __name__ == "__main__":
    unittest.main()
