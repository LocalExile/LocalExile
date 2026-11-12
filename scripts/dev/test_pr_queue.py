"""Offline checks of pr_queue.py: a made-up CODEOWNERS and a scratch repository; nothing is fetched."""
import subprocess
import tempfile
import unittest
from pathlib import Path

import pr_queue

CODEOWNERS = """\
# a comment
AGENTS.md @lead
*.csproj @lead
/.github/ @lead
/src/Launcher/ @lead @second
/src/Launcher/Readme.md @second
/sql/updates/pending_auth/ @lead @second
"""

# (path, the pattern that owns it; None: nobody)
PATHS = [
    ("AGENTS.md", "AGENTS.md"),
    ("Modules/Core/AGENTS.md", "AGENTS.md"),
    ("tests/T/T.csproj", "*.csproj"),
    (".github/workflows/x.yml", "/.github/"),
    ("src/Launcher/Program.cs", "/src/Launcher/"),
    ("src/Launcher/Readme.md", "/src/Launcher/Readme.md"),      # the last match wins
    ("sql/updates/pending_auth/1.sql", "/sql/updates/pending_auth/"),
    ("src/LauncherTools/Program.cs", None),
    ("docs/.github/x.yml", None),                               # a leading slash anchors at the root
    ("sql/updates/pending_world/1.sql", None),
    ("src/Game.cs", None),
]


class PrQueueTests(unittest.TestCase):
    def test_owners_and_the_authors_moves_are_read_as_github_reads_them(self):
        rules = pr_queue.owner_rules(CODEOWNERS)
        for path, expected in PATHS:
            with self.subTest(path=path):
                hit = pr_queue.owners_of(path, rules)
                self.assertEqual(hit[0] if hit else None, expected)
        self.assertEqual(pr_queue.owners_of("src/Launcher/Program.cs", rules)[1], ["lead", "second"])

        rows = [("labeled", "2026-01-01T10:00:00Z", "bug", "lead"),
                ("committed", "2026-01-02T09:00:00Z", "", ""),
                ("labeled", "2026-01-02T10:00:00Z", "needs-author", "lead"),
                ("commented", "2026-01-02T11:00:00Z", "", "lead")]
        self.assertEqual(pr_queue.moves_since_label(rows, "author"),
                         {"set": "2026-01-02", "pushed": False, "replied": False})
        rows += [("commented", "2026-01-03T08:00:00Z", "", "author"), ("committed", "2026-01-03T09:00:00Z", "", "")]
        self.assertEqual(pr_queue.moves_since_label(rows, "author"),
                         {"set": "2026-01-02", "pushed": True, "replied": True})
        self.assertIsNone(pr_queue.moves_since_label(rows[:2], "author"))

    def test_conflicts_stacks_and_protected_paths_add_up_to_the_labels(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)

            def git(*args):
                done = subprocess.run(["git", "-C", folder, "-c", "user.name=t", "-c", "user.email=t@localhost", *args],
                                      check=True, capture_output=True, text=True)
                return done.stdout.strip()

            def commit(message, **files):
                for path, text in files.items():
                    path = path.replace("__", "/")
                    (root / path).parent.mkdir(parents=True, exist_ok=True)
                    (root / path).write_text(text, encoding="utf-8", newline="\n")
                git("add", "-A")
                git("commit", "-q", "-m", message)
                return git("rev-parse", "HEAD")

            git("init", "-q", "-b", "main")
            start = commit("start", **{".gitattributes": "status.md merge=union\n",
                                       ".github__CODEOWNERS": "/src/Launcher/ @lead\n",
                                       "status.md": "- one\n", "src__Game.cs": "a\nb\nc\n",
                                       "src__Launcher__Program.cs": "x\n"})
            # Four pull requests cut from the start: 1 edits Game.cs, 2 sits on 1, 3 only adds a status line and
            # touches the launcher, 4 is unrelated.
            git("checkout", "-q", "-b", "one", start)
            one = commit("one", **{"src__Game.cs": "a\nfrom one\nc\n"})
            two = commit("two", **{"src__Other.cs": "two\n"})
            git("checkout", "-q", "-b", "three", start)
            three = commit("three", **{"status.md": "- one\n- three\n", "src__Launcher__Program.cs": "y\n"})
            git("checkout", "-q", "-b", "four", start)
            four = commit("four", **{"src__New.cs": "four\n"})
            # main moves on: the same line of Game.cs, and a status line of its own.
            git("checkout", "-q", "main")
            main = commit("main moves", **{"src__Game.cs": "a\nfrom main\nc\n", "status.md": "- one\n- main\n"})

            pulls = {1: {"head": one}, 2: {"head": two}, 3: {"head": three}, 4: {"head": four}}
            queue = pr_queue.Queue(root, main, pulls)
            labels = {number: sorted(pr_queue.wanted_labels(queue.entry(number), pulls[number])) for number in pulls}
            self.assertEqual(labels, {1: ["needs-author"], 2: ["BLOCKED"], 3: ["protected-files"], 4: []})
            self.assertEqual(queue.entry(1)["conflicts"], ["src/Game.cs"])
            self.assertEqual((queue.entry(2)["stackedOn"], queue.entry(2)["inherited"]), (1, True))
            self.assertEqual(queue.entry(3)["conflicts"], [])      # the status list keeps both sides' lines
            self.assertEqual(sorted(pr_queue.wanted_labels(queue.entry(4), {"review": "CHANGES_REQUESTED"})),
                             ["needs-author"])


if __name__ == "__main__":
    unittest.main()
