#!/usr/bin/env python3
"""Offline unit tests for .github/scripts/check-pr.py."""
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parent / "check-pr.py"
SPEC = importlib.util.spec_from_file_location("check_pr", SCRIPT_PATH)
check_pr = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(check_pr)

VALID_BODY = """## What changes

Add packet validation for inventory move requests.

## Layer

Framework (`src/GameServer.Gateway`)

## Issue

Closes #42

## Checks

- Base commit of `main`: abc1234
- Head commit: def5678
- [x] `dotnet build GameServer.slnx`
"""


def run_git(repo, *args):
    subprocess.run(
        ["git", "-C", str(repo), *args],
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )


def make_repo(root):
    repo = Path(root) / "repo"
    repo.mkdir()
    run_git(repo, "init", "-b", "main")
    run_git(repo, "config", "user.name", "Test User")
    run_git(repo, "config", "user.email", "test@example.com")
    (repo / "README.md").write_text("# Repo\nLine 2\n", encoding="utf-8")
    (repo / "src").mkdir()
    (repo / "src" / "Code.cs").write_text("class Code {}\n", encoding="utf-8")
    (repo / "tests").mkdir()
    (repo / "tests" / "CodeTests.cs").write_text("class CodeTests {}\n", encoding="utf-8")
    (repo / ".github").mkdir()
    (repo / ".github" / "CODEOWNERS").write_text(
        "AGENTS.md @Wrathen\n"
        "/.github/ @Wrathen\n"
        "*.csproj @Wrathen\n"
        "/src/LocalExile/ @Wrathen @evrydayjunglist\n",
        encoding="utf-8",
    )
    run_git(repo, "add", ".")
    run_git(repo, "commit", "-m", "chore: initial")
    run_git(repo, "checkout", "-b", "feature")
    return repo


class TitleValidationTests(unittest.TestCase):
    def test_valid_titles(self):
        valid = [
            "feat: add inventory sync",
            "fix(gateway): handle empty buffer",
            "chore(ci)!: drop legacy workflow",
            "docs(agents/status.md): update trade section",
            "refactor(core_v2): simplify codec",
            "revert: undo broken packet change",
        ]
        for title in valid:
            with self.subTest(title=title):
                self.assertEqual(check_pr.validate_title(title), [])

    def test_invalid_titles(self):
        invalid = [
            "",
            "Add inventory sync",
            "FEAT: uppercase type",
            "unknown: unsupported type",
            "feat(BAD): uppercase scope",
            "feat:",
            "feat:    ",
            "feat: line1\nline2",
            "feat: " + ("a" * 115),
        ]
        for title in invalid:
            with self.subTest(title=title):
                self.assertTrue(check_pr.validate_title(title))


class BodyValidationTests(unittest.TestCase):
    def test_valid_body_has_no_errors_or_warnings(self):
        errors, warnings = check_pr.validate_body(VALID_BODY)
        self.assertEqual(errors, [])
        self.assertEqual(warnings, [])

    def test_empty_body_fails(self):
        errors, _ = check_pr.validate_body("   \n\n ")
        self.assertTrue(errors)

    def test_missing_or_blank_what_changes_fails(self):
        body = "## What changes\n\n<!-- note -->\n\n## Layer\n\nFramework\n\n## Checks\n\n- [x] Done\n"
        errors, _ = check_pr.validate_body(body)
        self.assertTrue(any("What changes" in e for e in errors))

    def test_untouched_layer_prompt_fails(self):
        body = (
            "## What changes\n\nFix bug.\n\n"
            "## Layer\n\n"
            "Framework, Module, League or Mod, and the folder\n"
            "([What goes where](https://github.com/LocalExile/LocalExile/blob/main/AGENTS.md#what-goes-where)):\n\n"
            "## Checks\n\n- [x] `dotnet build GameServer.slnx`\n"
        )
        errors, warnings = check_pr.validate_body(body)
        self.assertTrue(any("Layer" in e for e in errors))
        self.assertEqual(warnings, [])

    def test_layer_prompt_with_answer_passes(self):
        body = (
            "## What changes\n\nFix bug.\n\n"
            "## Layer\n\n"
            "Framework, Module, League or Mod, and the folder\n"
            "([What goes where](https://github.com/LocalExile/LocalExile/blob/main/AGENTS.md#what-goes-where)):\n"
            "Module (`Modules/Core/Combat`)\n\n"
            "## Checks\n\n- [x] `dotnet build GameServer.slnx`\n"
        )
        errors, warnings = check_pr.validate_body(body)
        self.assertEqual(errors, [])
        self.assertEqual(warnings, [])

    def test_checks_advisory_warnings(self):
        missing_checks = "## What changes\n\nFix.\n\n## Layer\n\nFramework\n"
        errors, warnings = check_pr.validate_body(missing_checks)
        self.assertEqual(errors, [])
        self.assertTrue(any("Checks" in w for w in warnings))

        unchecked = (
            "## What changes\n\nFix.\n\n## Layer\n\nFramework\n\n"
            "## Checks\n\n- Base commit of `main`:\n- Head commit:\n- [ ] `dotnet build GameServer.slnx`\n"
        )
        errors, warnings = check_pr.validate_body(unchecked)
        self.assertEqual(errors, [])
        self.assertTrue(any("Checks" in w for w in warnings))

        commit_only = (
            "## What changes\n\nFix.\n\n## Layer\n\nFramework\n\n"
            "## Checks\n\n- Base commit of `main`: 1234567\n- [ ] `dotnet build GameServer.slnx`\n"
        )
        errors, warnings = check_pr.validate_body(commit_only)
        self.assertEqual(errors, [])
        self.assertEqual(warnings, [])


class GitDiffChecksTests(unittest.TestCase):
    def test_micro_pr_tiny_doc_edit_warns(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            repo = make_repo(tmp)
            (repo / "README.md").write_text("# Repo\nLine 2 fixed\n", encoding="utf-8")
            run_git(repo, "commit", "-am", "docs: typo")

            result, _ = check_pr.evaluate_pr(
                "main",
                "feature",
                "docs: fix typo in readme",
                VALID_BODY,
                repo / ".github" / "CODEOWNERS",
                cwd=repo,
            )
            self.assertTrue(result["ok"])
            self.assertFalse(result["protected"])
            self.assertTrue(any("CONTRIBUTING.md" in w for w in result["warnings"]))

    def test_micro_pr_tiny_code_with_doc_test_churn_warns(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            repo = make_repo(tmp)
            (repo / "src" / "Code.cs").write_text("class Code { int X => 1; }\n", encoding="utf-8")
            (repo / "tests" / "CodeTests.cs").write_text(
                "class CodeTests {\n" + "".join(f"  void T{i}() {{}}\n" for i in range(22)) + "}\n",
                encoding="utf-8",
            )
            run_git(repo, "commit", "-am", "fix: small tweak with test churn")

            result, _ = check_pr.evaluate_pr(
                "main",
                "feature",
                "fix: small tweak",
                VALID_BODY,
                repo / ".github" / "CODEOWNERS",
                cwd=repo,
            )
            self.assertTrue(result["ok"])
            self.assertTrue(any("CONTRIBUTING.md" in w for w in result["warnings"]))

    def test_substantive_code_change_does_not_warn(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            repo = make_repo(tmp)
            (repo / "src" / "Code.cs").write_text(
                "class Code {\n" + "".join(f"  int P{i}() => {i};\n" for i in range(10)) + "}\n",
                encoding="utf-8",
            )
            (repo / "tests" / "CodeTests.cs").write_text(
                "class CodeTests {\n" + "".join(f"  void T{i}() {{}}\n" for i in range(25)) + "}\n",
                encoding="utf-8",
            )
            run_git(repo, "commit", "-am", "feat: substantive change")

            result, _ = check_pr.evaluate_pr(
                "main",
                "feature",
                "feat: substantive change",
                VALID_BODY,
                repo / ".github" / "CODEOWNERS",
                cwd=repo,
            )
            self.assertTrue(result["ok"])
            self.assertEqual(result["warnings"], [])

    def test_codeowners_protected_paths(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            repo = make_repo(tmp)
            (repo / "AGENTS.md").write_text("# Agents\n", encoding="utf-8")
            run_git(repo, "add", "AGENTS.md")
            run_git(repo, "commit", "-m", "docs: update agents")

            result, report = check_pr.evaluate_pr(
                "main",
                "feature",
                "docs: update agent rules",
                VALID_BODY,
                repo / ".github" / "CODEOWNERS",
                cwd=repo,
            )
            self.assertTrue(result["ok"])
            self.assertTrue(result["protected"])
            self.assertIn("AGENTS.md", report)

    def test_cli_isolated_mode_and_sanitized_report(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            repo = make_repo(tmp)
            (repo / "src" / "Code.cs").write_text(
                "class Code {\n" + "".join(f"  int P{i}() => {i};\n" for i in range(8)) + "}\n",
                encoding="utf-8",
            )
            run_git(repo, "commit", "-am", "feat: code")

            body_file = Path(tmp) / "body.md"
            json_out = Path(tmp) / "result.json"
            report_out = Path(tmp) / "report.md"
            secret_marker = "RAW_BODY_TOKEN_@someone_<script>"
            body_file.write_text(f"## Random\n\n{secret_marker}\n", encoding="utf-8")

            proc = subprocess.run(
                [
                    sys.executable,
                    "-I",
                    str(SCRIPT_PATH),
                    "--base",
                    "main",
                    "--head",
                    "feature",
                    "--title=-bad-title-@attacker",
                    "--body-file",
                    str(body_file),
                    "--codeowners",
                    str(repo / ".github" / "CODEOWNERS"),
                    "--json-out",
                    str(json_out),
                    "--report",
                    str(report_out),
                ],
                cwd=repo,
                capture_output=True,
                text=True,
                encoding="utf-8",
            )
            self.assertEqual(proc.returncode, 1)
            payload = json.loads(json_out.read_text(encoding="utf-8"))
            report_text = report_out.read_text(encoding="utf-8")
            self.assertFalse(payload["ok"])
            self.assertFalse(payload["protected"])
            self.assertNotIn("-bad-title-@attacker", report_text)
            self.assertNotIn(secret_marker, report_text)
            self.assertNotIn("-bad-title-@attacker", json.dumps(payload))
            self.assertNotIn(secret_marker, json.dumps(payload))


if __name__ == "__main__":
    unittest.main()
