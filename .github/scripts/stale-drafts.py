#!/usr/bin/env python3
"""Mark and close inactive draft pull requests (2 days to stale, 7 days total to close).

Only reads GitHub PR metadata and timelines via `gh api`; never checks out PR code or echoes raw PR text.
"""
import argparse
import json
import os
import subprocess
import sys
from datetime import datetime, timedelta, timezone

ACTIVITY_EVENTS = frozenset({"committed", "head_ref_force_pushed", "commented", "reviewed", "edited"})
BOT_LOGINS = frozenset({"github-actions[bot]"})


def parse_iso(value):
    """Parse an ISO-8601 UTC timestamp string into an aware datetime."""
    if value.endswith("Z"):
        value = value[:-1] + "+00:00"
    dt = datetime.fromisoformat(value)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def run_gh(args):
    """Run gh CLI and return stdout; raise RuntimeError on failure."""
    result = subprocess.run(["gh", *args], check=False, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or f"gh exited with code {result.returncode}")
    return result.stdout


def list_open_drafts(repo):
    """Fetch open pull requests and return (number, draft, updated_at, labels) dicts for drafts."""
    out = run_gh([
        "api", "--paginate", f"repos/{repo}/pulls?state=open&per_page=100",
        "--jq", ".[] | {number: .number, draft: .draft, updated_at: .updated_at, labels: [.labels[].name]}",
    ])
    pulls = []
    for line in out.splitlines():
        line = line.strip()
        if line:
            pulls.append(json.loads(line))
    return pulls


def timeline_rows(repo, number):
    """Return (event, timestamp, label_name, actor_login) tuples for a pull request timeline."""
    out = run_gh([
        "api", "--paginate", f"repos/{repo}/issues/{number}/timeline",
        "--jq", '.[] | [(.event // ""), (.created_at // .committer.date // ""), (.label.name // ""), '
                '(.actor.login // .user.login // "")] | @tsv',
    ])
    rows = []
    for line in out.splitlines():
        if line:
            parts = (line.split("\t") + ["", "", "", ""])[:4]
            rows.append(tuple(parts))
    return rows


def decide_action(pr, rows, now, stale_days=2, close_days=7, exempt_label="BLOCKED", stale_label="stale"):
    """Decide one of: 'noop', 'mark_stale', 'unstale', or 'close' for a pull request."""
    if not pr.get("draft"):
        return "noop"
    labels = set(pr.get("labels") or [])
    if exempt_label and exempt_label in labels:
        return "noop"

    updated_at = parse_iso(pr["updated_at"])
    if stale_label not in labels:
        if now - updated_at >= timedelta(days=stale_days):
            return "mark_stale"
        return "noop"

    stale_at = max(
        (parse_iso(ts) for event, ts, label, _ in rows if event == "labeled" and label == stale_label and ts),
        default=None,
    )
    if stale_at is not None:
        for event, ts, _, actor in rows:
            if not ts or event not in ACTIVITY_EVENTS or actor in BOT_LOGINS:
                continue
            if parse_iso(ts) > stale_at:
                return "unstale"
        if now - stale_at >= timedelta(days=max(0, close_days - stale_days)):
            return "close"
        return "noop"

    if now - updated_at >= timedelta(days=close_days):
        return "close"
    return "noop"


def apply_action(repo, number, action, stale_days, close_days, stale_label="stale", dry_run=False):
    """Apply 'mark_stale', 'unstale', or 'close' via gh api."""
    if action == "noop" or dry_run:
        return
    if action == "mark_stale":
        run_gh(["api", "-X", "POST", f"repos/{repo}/issues/{number}/labels", "-f", f"labels[]={stale_label}"])
        message = (
            f"This draft pull request has had no activity for {stale_days} days and has been marked stale. "
            f"Pushing a commit, commenting, or marking it ready resets the timer; otherwise it will close "
            f"at day {close_days} if still a draft (it can be reopened anytime)."
        )
        run_gh(["api", "-X", "POST", f"repos/{repo}/issues/{number}/comments", "-f", f"body={message}"])
    elif action == "unstale":
        run_gh(["api", "-X", "DELETE", f"repos/{repo}/issues/{number}/labels/{stale_label}"])
    elif action == "close":
        message = (
            f"Closing this draft pull request after {close_days} days without activity. "
            "It can be reopened anytime when ready."
        )
        run_gh(["api", "-X", "POST", f"repos/{repo}/issues/{number}/comments", "-f", f"body={message}"])
        run_gh(["api", "-X", "PATCH", f"repos/{repo}/pulls/{number}", "-f", "state=closed"])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=os.environ.get("REPO", "LocalExile/LocalExile"), help="owner/name on GitHub")
    parser.add_argument("--stale-days", type=int, default=2, help="Days of inactivity before marking a draft stale")
    parser.add_argument("--close-days", type=int, default=7, help="Total days of inactivity before closing a draft")
    parser.add_argument("--exempt-label", default="BLOCKED", help="Label that exempts a draft PR")
    parser.add_argument("--stale-label", default="stale", help="Label applied to stale draft PRs")
    parser.add_argument("--max-ops", type=int, default=60, help="Maximum mutating PR operations per run")
    parser.add_argument("--dry-run", action="store_true", help="Print planned actions without changing GitHub")
    args = parser.parse_args(argv)

    now = datetime.now(timezone.utc)
    ops = 0
    for pr in list_open_drafts(args.repo):
        if ops >= args.max_ops:
            break
        if not pr.get("draft"):
            continue
        labels = set(pr.get("labels") or [])
        if args.exempt_label and args.exempt_label in labels:
            continue
        rows = timeline_rows(args.repo, pr["number"]) if args.stale_label in labels else []
        action = decide_action(
            pr,
            rows,
            now,
            stale_days=args.stale_days,
            close_days=args.close_days,
            exempt_label=args.exempt_label,
            stale_label=args.stale_label,
        )
        if action != "noop":
            print(f"#{pr['number']}: {action}")
            apply_action(
                args.repo,
                pr["number"],
                action,
                stale_days=args.stale_days,
                close_days=args.close_days,
                stale_label=args.stale_label,
                dry_run=args.dry_run,
            )
            ops += 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
