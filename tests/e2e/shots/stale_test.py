"""stale.py's own tests. They take seconds and need Python 3.11 (and git, for the plan's):

    python3 -m unittest discover -s tests/e2e/shots -p stale_test.py

Most build a small repo in a temporary folder: a map, a test_shots.py, its pictures and a few product files, all taken
at the first commit. The last ones check the real map against the real tree."""
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stale  # noqa: E402

SHOTS = '''
import pytest

LIMIT = 3


def by_hand(shooter, page, name):
    shooter.window(page, name)


def helper(page):
    return page


def test_step_01_setup(shooter):
    by_hand(shooter, None, "made-on-the-way")


def test_dashboard(shooter):
    shooter.window(helper(None), "dashboard")


def test_card(shooter):
    shooter.detail(None, None, "card", pad=LIMIT)


@pytest.mark.parametrize("sid,name", [("one", "section-one"), ("two", "section-two")])
def test_section(shooter, sid, name):
    shooter.detail(None, None, name)


def test_login(shooter):
    shooter.window(None, "login")
'''

MAP = '''
version = 1
all = ["src/theme.css"]
window = ["src/layout/**"]
window_except = ["login"]

[only_within]
"src/js/app.js" = { from = "^// -+ Charts" }

[sets]
page = ["src/pages/Page.razor"]

[pictures]
made-on-the-way = ["src/steps/**"]
dashboard = ["@page", "src/pages/Home.razor"]
card = ["src/cards/Card.razor", "src/js/app.js"]
section-one = ["@page", "src/sections/One.razor"]
section-two = ["@page", "src/sections/Two.razor"]
login = ["src/pages/Login.razor"]
'''

FILES = {
    "src/theme.css": "body {}\n",
    "src/layout/Nav.razor": "<nav/>\n",
    "src/pages/Page.razor": "<page/>\n",
    "src/pages/Home.razor": "<home/>\n",
    "src/pages/Login.razor": "<login/>\n",
    "src/cards/Card.razor": "<card/>\n",
    "src/sections/One.razor": "<one/>\n",
    "src/sections/Two.razor": "<two/>\n",
    "src/steps/Step.razor": "<step/>\n",
    "src/js/app.js": "function upload() {}\n// ---- Charts\nfunction chart() {}\n",
    "src/Other.cs": "class Other {}\n",
    "tests/e2e/shots/conftest.py": "W, H = 1440, 900\n",
}
PICTURES = ["card", "dashboard", "login", "made-on-the-way", "section-one", "section-two"]


def write(root, path, data):
    p = Path(root) / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(data.encode() if isinstance(data, str) else data)


def tree(root, shots=SHOTS, picture_map=MAP):
    for p, t in FILES.items():
        write(root, p, t)
    write(root, stale.MAP, picture_map)
    write(root, stale.SHOTS, shots)
    for n in PICTURES:
        write(root, f"{stale.IMAGES}/{n}.webp", f"RIFF {n}".encode())


class Repo:
    """A repo whose pictures were all taken from its first commit (taken.toml, committed after it)."""

    def __init__(self):
        self.dir = Path(tempfile.mkdtemp(prefix="stale-test-"))
        self.git("init", "-q")
        for k, v in (("user.email", "test@example.com"), ("user.name", "Test"), ("core.autocrlf", "false")):
            self.git("config", k, v)
        tree(self.dir)
        self.first = self.commit("first")
        stale.record(self.dir, self.first, PICTURES)
        self.commit("taken")

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.dir), *args], check=True, capture_output=True).stdout.decode().strip()

    def write(self, path, data):
        write(self.dir, path, data)

    def commit(self, message):
        self.git("add", "-A")
        self.git("commit", "-q", "--allow-empty", "-m", message)
        return self.git("rev-parse", "HEAD")

    def stale(self, **kw):
        return stale.plan(self.dir, "HEAD", self.dir, **kw).stale


@unittest.skipUnless(shutil.which("git"), "needs git")
class PlanTests(unittest.TestCase):
    def setUp(self):
        self.repo = Repo()

    def tearDown(self):
        shutil.rmtree(self.repo.dir, ignore_errors=True)

    def test_nothing_changed_nothing_is_stale(self):
        self.assertEqual({}, self.repo.stale())

    def test_a_file_a_picture_shows_makes_that_picture_stale_and_no_other(self):
        self.repo.write("src/cards/Card.razor", "<card class=new/>\n")
        self.repo.commit("card")
        stale_now = self.repo.stale()
        self.assertEqual(["card"], list(stale_now))
        self.assertEqual(f"src/cards/Card.razor changed since {self.repo.first[:7]}", stale_now["card"])

    def test_a_set_reaches_every_picture_that_uses_it(self):
        self.repo.write("src/pages/Page.razor", "<page v2/>\n")
        self.repo.commit("page")
        self.assertEqual(["dashboard", "section-one", "section-two"], sorted(self.repo.stale()))

    def test_the_layout_makes_whole_window_pictures_stale_but_not_details_or_those_without_it(self):
        self.repo.write("src/layout/Nav.razor", "<nav v2/>\n")
        self.repo.commit("nav")
        self.assertEqual(["dashboard", "made-on-the-way"], sorted(self.repo.stale()))

    def test_the_look_every_picture_shares_makes_every_picture_stale(self):
        self.repo.write("src/theme.css", "body { color: red }\n")
        self.repo.commit("theme")
        self.assertEqual(PICTURES, sorted(self.repo.stale()))

    def test_a_file_in_only_within_counts_only_for_a_change_in_its_section(self):
        self.repo.write("src/js/app.js", "function upload(file) {}\n// ---- Charts\nfunction chart() {}\n")
        self.repo.commit("upload")
        self.assertEqual({}, self.repo.stale())
        self.repo.write("src/js/app.js", "function upload(file) {}\n// ---- Charts\nlet area = 0;\nfunction chart() {}\n")
        self.repo.commit("chart")
        self.assertEqual(["card"], list(self.repo.stale()))

    def test_a_file_no_picture_shows_makes_none_stale(self):
        self.repo.write("src/Other.cs", "class Other { int x; }\n")
        self.repo.commit("other")
        self.assertEqual({}, self.repo.stale())

    def test_a_changed_shot_makes_its_picture_stale(self):
        self.repo.write(stale.SHOTS, SHOTS.replace('"card", pad=LIMIT)', '"card", pad=0)'))
        self.repo.commit("shot")
        self.assertEqual({"card": f"its shot (test_card) changed since {self.repo.first[:7]}"}, self.repo.stale())

    def test_a_changed_helper_reaches_the_shots_that_use_it(self):
        self.repo.write(stale.SHOTS, SHOTS.replace("return page", "return page or None"))
        self.repo.commit("helper")
        self.assertEqual(["dashboard"], list(self.repo.stale()))

    def test_a_changed_constant_reaches_the_shots_that_use_it(self):
        self.repo.write(stale.SHOTS, SHOTS.replace("LIMIT = 3", "LIMIT = 4"))
        self.repo.commit("constant")
        self.assertEqual(["card"], list(self.repo.stale()))

    def test_a_changed_helper_that_takes_the_picture_reaches_the_pictures_taken_through_it(self):
        self.repo.write(stale.SHOTS, SHOTS.replace("shooter.window(page, name)", "shooter.window(page, name, mouse=False)"))
        self.repo.commit("by_hand")
        self.assertEqual(["made-on-the-way"], list(self.repo.stale()))

    def test_a_parametrized_shot_that_changed_makes_each_of_its_pictures_stale(self):
        self.repo.write(stale.SHOTS, SHOTS.replace("shooter.detail(None, None, name)", "shooter.detail(None, None, name, pad=0)"))
        self.repo.commit("sections")
        self.assertEqual(["section-one", "section-two"], sorted(self.repo.stale()))

    def test_a_change_in_the_tree_not_committed_yet_counts(self):
        self.repo.write("src/sections/One.razor", "<one v2/>\n")
        self.assertEqual(["section-one"], list(self.repo.stale()))

    def test_a_change_in_the_tree_counts_even_once_its_picture_was_taken_again(self):
        # Taken again and not committed yet: whether One.razor changed again since can't be told, so it's taken again
        # (it isn't kept when it looks the same).
        self.repo.write("src/sections/One.razor", "<one v2/>\n")
        self.repo.write(f"{stale.IMAGES}/section-one.webp", b"RIFF section-one, again")
        self.assertEqual(["section-one"], list(self.repo.stale()))

    def test_a_file_gone_from_the_tree_counts(self):
        (self.repo.dir / "src/sections/Two.razor").unlink()
        self.assertEqual(["section-two"], list(self.repo.stale()))

    def test_a_picture_docs_images_doesnt_have_is_stale(self):
        (self.repo.dir / stale.IMAGES / "card.webp").unlink()
        self.assertEqual({"card": "not in docs/images"}, self.repo.stale())

    def test_since_notes_changed_set_up_steps_and_the_files_the_shots_run_with(self):
        self.repo.write(stale.SHOTS, SHOTS.replace('by_hand(shooter, None, "made-on-the-way")',
                                                   'by_hand(shooter, None, "made-on-the-way")\n    print("set up")'))
        self.repo.write("tests/e2e/shots/conftest.py", "W, H = 1440, 1000\n")
        self.repo.commit("a step and the window")
        result = stale.plan(self.repo.dir, "HEAD", self.repo.dir, since_ref=self.repo.first)
        self.assertEqual(["made-on-the-way"], list(result.stale))
        self.assertEqual(2, len(result.notes))
        self.assertIn("set-up steps changed since", result.notes[0])
        self.assertIn("test_step_01_setup", result.notes[0])
        self.assertTrue(result.notes[1].startswith("tests/e2e/shots/conftest.py changed since"), result.notes[1])
        self.assertEqual([], stale.plan(self.repo.dir, "HEAD", self.repo.dir, since_ref="HEAD").notes)

    def test_line_endings_alone_arent_a_change(self):
        self.repo.write("src/cards/Card.razor", "<card/>\r\n")
        self.assertEqual({}, self.repo.stale())

    def test_a_copy_of_the_tree_elsewhere_is_compared_with_the_head(self):
        copy = Path(tempfile.mkdtemp(prefix="stale-tree-"))
        try:
            shutil.copytree(self.repo.dir, copy, dirs_exist_ok=True, ignore=shutil.ignore_patterns(".git"))
            write(copy, "src/cards/Card.razor", "<card v2/>\n")
            self.assertEqual(["card"], list(stale.plan(self.repo.dir, "HEAD", copy).stale))
        finally:
            shutil.rmtree(copy, ignore_errors=True)

    def test_a_picture_never_taken_is_stale(self):
        text = (self.repo.dir / stale.TAKEN).read_text(encoding="utf-8").replace(f'card = "{self.repo.first}"\n', "")
        self.repo.write(stale.TAKEN, text)
        self.repo.commit("card forgotten")
        self.assertEqual({"card": "never taken (taken.toml doesn't have it)"}, self.repo.stale())

    def test_also_and_all(self):
        self.assertEqual({"card": "asked for"}, self.repo.stale(also=["card"]))
        self.assertEqual(PICTURES, sorted(self.repo.stale(every=True)))
        with self.assertRaises(stale.Refused):
            self.repo.stale(also=["nowhere"])

    def test_recording_a_run_makes_its_pictures_fresh_and_leaves_the_others(self):
        self.repo.write("src/cards/Card.razor", "<card v2/>\n")
        head = self.repo.commit("card")
        self.assertEqual(["card"], list(self.repo.stale()))
        stale.record(self.repo.dir, head, ["card"])
        self.repo.commit("card taken again")
        self.assertEqual({}, self.repo.stale())
        taken = stale.load_taken((self.repo.dir / stale.TAKEN).read_text(encoding="utf-8"))
        self.assertEqual((head, self.repo.first), (taken["card"], taken["dashboard"]))


class ShotsTests(unittest.TestCase):
    def test_each_picture_its_kind_and_the_test_that_takes_it(self):
        shots, problems = stale.shots_of(SHOTS)
        self.assertEqual([], problems)
        self.assertEqual(PICTURES, sorted(shots))
        self.assertEqual(stale.Shot("window", "test_step_01_setup", False), shots["made-on-the-way"])
        self.assertEqual(stale.Shot("window", "test_dashboard", False), shots["dashboard"])
        self.assertEqual(stale.Shot("detail", "test_card", False), shots["card"])
        self.assertEqual(stale.Shot("detail", "test_section", True), shots["section-two"])

    def test_a_picture_two_tests_take_is_a_problem(self):
        _, problems = stale.shots_of(SHOTS + '\n\ndef test_again(shooter):\n    shooter.window(None, "card")\n')
        self.assertEqual(["card is taken by both test_card and test_again"], problems)

    def test_k_runs_every_step_and_the_shots_of_the_rest(self):
        shots, _ = stale.shots_of(SHOTS)
        self.assertEqual("step or test_card or section-two", stale.k_expression(["section-two", "made-on-the-way", "card"], shots))


class MapTests(unittest.TestCase):
    def test_sets_expand_and_the_window_leaves_out_the_pictures_without_it(self):
        m, problems = stale.load_map(MAP)
        self.assertEqual([], problems)
        self.assertEqual(["src/pages/Page.razor", "src/sections/One.razor"], m.pictures["section-one"])
        self.assertEqual(["src/theme.css", "src/layout/**", "src/pages/Page.razor", "src/pages/Home.razor"], m.globs_for("dashboard", "window"))
        self.assertEqual(["src/theme.css", "src/pages/Login.razor"], m.globs_for("login", "window"))

    def test_a_set_that_uses_itself_or_isnt_there_is_a_problem(self):
        _, problems = stale.load_map('[sets]\na = ["@b"]\nb = ["@a"]\n\n[pictures]\nx = ["@a", "@nowhere"]\n')
        self.assertIn("set b: the set a uses itself", problems)
        self.assertIn("x: there's no set nowhere", problems)

    def test_what_the_map_misses_is_an_error(self):
        root = Path(tempfile.mkdtemp(prefix="stale-check-"))
        try:
            tree(root, picture_map=MAP + 'extra = ["src/nowhere/**"]\n')
            stale.record(root, "0" * 40, PICTURES)
            write(root, f"{stale.IMAGES}/unmapped.webp", b"RIFF")
            errors, warnings = stale.check(root)
            self.assertEqual(["docs/images/unmapped.webp isn't in pictures.toml", "extra: no shot in test_shots.py takes it",
                              "extra: src/nowhere/** matches no file"], errors)
            self.assertEqual(["extra isn't in taken.toml: the next run takes it"], warnings)
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_the_real_map_fits_the_real_tree(self):
        # Warnings aren't failures: a new shot isn't taken or in taken.toml until its first run.
        errors, _ = stale.check(stale.ROOT)
        self.assertEqual([], errors)


if __name__ == "__main__":
    unittest.main()
