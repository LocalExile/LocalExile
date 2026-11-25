"""Offline tests for .github/scripts/stale-drafts.py."""
import importlib.util
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parent / "stale-drafts.py"
SPEC = importlib.util.spec_from_file_location("stale_drafts", SCRIPT_PATH)
stale_drafts = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(stale_drafts)


def iso(dt):
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class StaleDraftsTests(unittest.TestCase):
    def setUp(self):
        self.now = datetime(2026, 11, 25, 12, 0, 0, tzinfo=timezone.utc)

    def test_ignores_non_draft_and_blocked_draft(self):
        old = iso(self.now - timedelta(days=10))
        self.assertEqual(stale_drafts.decide_action({"draft": False, "updated_at": old, "labels": []}, [], self.now), "noop")
        self.assertEqual(
            stale_drafts.decide_action({"draft": True, "updated_at": old, "labels": ["BLOCKED"]}, [], self.now),
            "noop",
        )

    def test_marks_draft_stale_after_2_days(self):
        recent = iso(self.now - timedelta(days=1, hours=23))
        old = iso(self.now - timedelta(days=2))
        self.assertEqual(
            stale_drafts.decide_action({"draft": True, "updated_at": recent, "labels": []}, [], self.now),
            "noop",
        )
        self.assertEqual(
            stale_drafts.decide_action({"draft": True, "updated_at": old, "labels": []}, [], self.now),
            "mark_stale",
        )

    def test_unstales_on_non_bot_activity_after_stale_label(self):
        stale_at = self.now - timedelta(days=3)
        pr = {"draft": True, "updated_at": iso(stale_at), "labels": ["stale"]}
        rows_commit = [
            ("labeled", iso(stale_at), "stale", "github-actions[bot]"),
            ("committed", iso(stale_at + timedelta(hours=4)), "", ""),
        ]
        self.assertEqual(stale_drafts.decide_action(pr, rows_commit, self.now), "unstale")

        rows_comment = [
            ("labeled", iso(stale_at), "stale", "github-actions[bot]"),
            ("commented", iso(stale_at + timedelta(days=1)), "", "contributor"),
        ]
        self.assertEqual(stale_drafts.decide_action(pr, rows_comment, self.now), "unstale")

    def test_closes_at_day_7_when_only_bot_comment_or_no_activity(self):
        stale_at_day4 = self.now - timedelta(days=2)
        pr_day4 = {"draft": True, "updated_at": iso(stale_at_day4), "labels": ["stale"]}
        rows_day4 = [
            ("labeled", iso(stale_at_day4), "stale", "github-actions[bot]"),
            ("commented", iso(stale_at_day4 + timedelta(seconds=5)), "", "github-actions[bot]"),
        ]
        self.assertEqual(stale_drafts.decide_action(pr_day4, rows_day4, self.now), "noop")

        stale_at_day7 = self.now - timedelta(days=5)
        pr_day7 = {"draft": True, "updated_at": iso(stale_at_day7), "labels": ["stale"]}
        rows_day7 = [
            ("labeled", iso(stale_at_day7), "stale", "github-actions[bot]"),
            ("commented", iso(stale_at_day7 + timedelta(seconds=5)), "", "github-actions[bot]"),
        ]
        self.assertEqual(stale_drafts.decide_action(pr_day7, rows_day7, self.now), "close")


if __name__ == "__main__":
    unittest.main()
