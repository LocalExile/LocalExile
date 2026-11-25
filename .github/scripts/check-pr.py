#!/usr/bin/env python3
"""Validate pull request title, template sections, diff hygiene, and CODEOWNERS protection.

Only reads git metadata and CLI inputs; never echoes raw PR title or body text into reports.
Exit code: 0 when no hard errors exist (advisory warnings may be present), 1 when any hard error exists.
"""
import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

MAX_TITLE_LENGTH = 120
TITLE_RE = re.compile(
    r"^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._/-]+\))?!?: .+\Z"
)
LAYER_TEMPLATE_RE = re.compile(
    r"Framework,\s*Module,\s*League or Mod,\s*and the folder"
    r"(?:\s*\(\[What goes where\]\([^)]*\)\))?\s*:?",
    re.IGNORECASE,
)
HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)
CHECKED_BOX_RE = re.compile(r"^[ \t]*[-*][ \t]*\[[xX]\]", re.MULTILINE)
COMMIT_LINE_RE = re.compile(
    r"^[ \t]*(?:[-*][ \t]*)?(?:\*\*)?(?:base|head)[ \t]+commit[^:\r\n]*:(?:\*\*)?[ \t]*([^\r\n]+)$",
    re.IGNORECASE | re.MULTILINE,
)
DOC_EXTENSIONS = (".md", ".rst", ".txt")


def safe(text):
    """Text reduced so it cannot format or mention anything in a comment."""
    return re.sub(r"[^A-Za-z0-9_./+ :=-]", "?", text)[:160]


def owner_rules(text):
    """The patterns of a CODEOWNERS file as (pattern, regex, owners), in the file's order."""
    found = []
    for raw in text.splitlines():
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        pattern, *owners = line.split()
        body = pattern.strip("/")
        anchored = pattern.startswith("/") or "/" in body
        regex = "".join(
            ".*" if part == "**" else "[^/]*" if part == "*" else re.escape(part)
            for part in re.split(r"(\*\*|\*)", body)
        )
        found.append(
            (
                pattern,
                re.compile(("^" if anchored else "^(?:.*/)?") + regex + "(?:/.*)?$"),
                [owner.lstrip("@") for owner in owners],
            )
        )
    return found


def owners_of(path, rules):
    """(pattern, owners) of the last pattern that matches, as GitHub reads the file; None when nothing does."""
    hit = None
    for pattern, regex, owners in rules:
        if regex.match(path):
            hit = (pattern, owners)
    return hit


def parse_sections(body):
    """Return a lowercase heading -> content mapping for level-2 markdown headings."""
    sections = {}
    current = None
    in_fence = False
    for line in body.splitlines():
        stripped = line.strip()
        if stripped.startswith("```") or stripped.startswith("~~~"):
            in_fence = not in_fence
        if not in_fence:
            heading = re.match(r"^(#{1,6})\s+(.+?)\s*#*$", stripped)
            if heading:
                level = len(heading.group(1))
                if level == 2:
                    current = heading.group(2).strip().lower()
                    sections.setdefault(current, [])
                    continue
                if level == 1:
                    current = None
                    continue
        if current is not None:
            sections[current].append(line)
    return {name: "\n".join(lines).strip() for name, lines in sections.items()}


def strip_comments(text):
    """Remove HTML comments from markdown text."""
    return HTML_COMMENT_RE.sub("", text)


def validate_title(title):
    """Validate Conventional Commits format and max length; never include raw title in error messages."""
    errors = []
    if "\n" in title or "\r" in title:
        errors.append("PR title must be a single line.")
        return errors
    if len(title) > MAX_TITLE_LENGTH:
        errors.append(f"PR title exceeds {MAX_TITLE_LENGTH} characters.")
    if not TITLE_RE.match(title) or not title.split(": ", 1)[1].strip():
        errors.append(
            "PR title must follow Conventional Commits: <type>[optional scope]: <subject> "
            "(allowed types: feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert)."
        )
    return errors


def validate_body(body):
    """Validate required PR template sections and optional checks advisory."""
    errors = []
    warnings = []
    if not body or not body.strip():
        errors.append("PR description is empty; fill in .github/pull_request_template.md.")
        return errors, warnings

    sections = parse_sections(body)

    what_changes = strip_comments(sections.get("what changes", "")).strip()
    if not what_changes:
        errors.append("## What changes section is missing or empty.")

    if "layer" not in sections:
        errors.append("## Layer section is missing.")
    else:
        layer_text = strip_comments(sections["layer"])
        layer_remaining = LAYER_TEMPLATE_RE.sub("", layer_text).strip(" \t\r\n:-")
        if not layer_remaining:
            errors.append("## Layer section is empty or still contains only the template prompt.")

    if "checks" not in sections:
        warnings.append("## Checks section is missing.")
    else:
        checks_text = strip_comments(sections["checks"])
        has_checked_box = bool(CHECKED_BOX_RE.search(checks_text))
        has_commit = any(
            m.group(1).strip().strip("`-_ ")
            for m in COMMIT_LINE_RE.finditer(checks_text)
        )
        if not has_checked_box and not has_commit:
            warnings.append(
                "## Checks section has neither a checked box ([x]) nor a filled Base commit / Head commit."
            )

    return errors, warnings


def run_git(args, cwd=None):
    """Execute a git command and return stdout text."""
    cmd = ["git"]
    if cwd is not None:
        cmd.extend(["-C", str(cwd)])
    cmd.extend(args)
    result = subprocess.run(cmd, check=True, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return result.stdout


def get_merge_base(base, head, cwd=None):
    """Return merge-base commit OID between base and head."""
    return run_git(["merge-base", base, head], cwd=cwd).strip()


def get_changed_files(merge_base, head, cwd=None):
    """Return non-renamed changed paths between merge_base and head."""
    out = run_git(["-c", "core.quotePath=false", "diff", "--name-only", "--no-renames", merge_base, head], cwd=cwd)
    return [line.rstrip("\r") for line in out.splitlines() if line.strip()]


def get_numstat(merge_base, head, cwd=None):
    """Return list of (added, deleted, path) from git diff --numstat."""
    out = run_git(["-c", "core.quotePath=false", "diff", "--numstat", "--no-renames", merge_base, head], cwd=cwd)
    entries = []
    for raw in out.splitlines():
        if not raw.strip():
            continue
        parts = raw.rstrip("\r").split("\t", 2)
        if len(parts) != 3:
            continue
        added_s, deleted_s, path = parts
        added = 0 if added_s == "-" else int(added_s)
        deleted = 0 if deleted_s == "-" else int(deleted_s)
        entries.append((added, deleted, path))
    return entries


def is_doc_path(path):
    """Return True if path is a documentation file."""
    return path.lower().endswith(DOC_EXTENSIONS)


def is_test_path(path):
    """Return True if path resides under a tests/ directory."""
    lower = path.lower()
    return lower.startswith("tests/") or "/tests/" in lower


def check_micro_pr(numstat_entries):
    """Return an advisory warning string if diff matches micro-PR / doc-churn heuristics, else None."""
    if not numstat_entries:
        return None
    code_added = 0
    doc_added = 0
    doc_deleted = 0
    test_added = 0
    all_docs = True

    for added, deleted, path in numstat_entries:
        if is_doc_path(path):
            doc_added += added
            doc_deleted += deleted
        else:
            all_docs = False
            if is_test_path(path):
                test_added += added
            else:
                code_added += added

    churn_with_tiny_code = code_added <= 5 and (doc_added + test_added) >= 20
    tiny_doc_only = all_docs and doc_added <= 5 and doc_deleted <= 5
    if churn_with_tiny_code or tiny_doc_only:
        return (
            "Micro-PR / doc-churn advisory: this PR changes very few non-doc, non-test code lines "
            "or makes a tiny doc-only edit. Consider batching related changes per CONTRIBUTING.md."
        )
    return None


def detect_protected_paths(changed_paths, codeowners_text):
    """Return list of (path, pattern, owners) for changed paths matching CODEOWNERS."""
    rules = owner_rules(codeowners_text)
    hits = []
    for path in changed_paths:
        hit = owners_of(path, rules)
        if hit:
            hits.append((path, hit[0], hit[1]))
    return hits


def build_report(errors, warnings, protected_hits):
    """Render a sanitized Markdown report with no raw PR title or body text."""
    lines = []
    if errors:
        lines.append(f"**Errors ({len(errors)})**")
        lines.append("")
        for err in errors:
            lines.append(f"- {err}")
        lines.append("")
    if warnings:
        lines.append(f"**Warnings ({len(warnings)})**")
        lines.append("")
        for warn in warnings:
            lines.append(f"- {warn}")
        lines.append("")
    if protected_hits:
        lines.append(f"**Protected paths ({len(protected_hits)})**")
        lines.append("")
        for path, pattern, owners in protected_hits[:40]:
            owner_list = ", ".join(safe(o) for o in owners) if owners else "none"
            lines.append(f"- `{safe(path)}` (rule `{safe(pattern)}`, owners: {owner_list})")
        if len(protected_hits) > 40:
            lines.append(f"- and {len(protected_hits) - 40} more")
        lines.append("")
    if not errors and not warnings:
        lines.append("All PR hygiene checks pass.")
        lines.append("")
    return "\n".join(lines)


def evaluate_pr(base, head, title, body, codeowners_path, cwd=None):
    """Run all PR checks and return (result_dict, report_markdown)."""
    errors = []
    warnings = []

    errors.extend(validate_title(title))
    body_errors, body_warnings = validate_body(body)
    errors.extend(body_errors)
    warnings.extend(body_warnings)

    merge_base = get_merge_base(base, head, cwd=cwd)
    changed_paths = get_changed_files(merge_base, head, cwd=cwd)
    numstat = get_numstat(merge_base, head, cwd=cwd)

    micro_warning = check_micro_pr(numstat)
    if micro_warning:
        warnings.append(micro_warning)

    codeowners_text = Path(codeowners_path).read_text(encoding="utf-8")
    protected_hits = detect_protected_paths(changed_paths, codeowners_text)
    protected = bool(protected_hits)

    result = {
        "ok": len(errors) == 0,
        "protected": protected,
        "errors": errors,
        "warnings": warnings,
    }
    report = build_report(errors, warnings, protected_hits)
    return result, report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, help="Base branch or commit ref")
    parser.add_argument("--head", required=True, help="Pull request head commit or ref")
    parser.add_argument("--title", required=True, help="Pull request title")
    parser.add_argument("--body-file", required=True, help="Path to file containing pull request body")
    parser.add_argument("--codeowners", default=".github/CODEOWNERS", help="Path to CODEOWNERS file")
    parser.add_argument("--json-out", help="Optional path to write JSON summary")
    parser.add_argument("--report", help="Optional path to write Markdown report")
    args = parser.parse_args()

    body = Path(args.body_file).read_text(encoding="utf-8", errors="replace")
    result, report = evaluate_pr(args.base, args.head, args.title, body, args.codeowners)

    if args.json_out:
        Path(args.json_out).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    if args.report:
        Path(args.report).write_text(report, encoding="utf-8")

    print(report)
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
