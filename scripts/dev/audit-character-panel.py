"""Inventory issue #75's panel dependencies without treating source matches as proof.

Run from the repository root. Reads extracted tables and tracked documentation/code;
writes only a local report under artifacts/ by default. Requires Python 3.9+.
"""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import subprocess


def git(*args):
    return subprocess.check_output(["git", *args], encoding="utf-8").strip()


def read_table(name):
    path = Path("data/tables") / (name + ".json")
    raw = path.read_bytes()
    return json.loads(raw.decode("utf-8-sig")), hashlib.sha256(raw).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path("artifacts/issue75-review/panel-inventory.json"))
    parser.add_argument("--ledger", type=Path, default=Path("Agents/docs/dev/issue75-stat-ledger.md"))
    args = parser.parse_args()
    if not Path("src/GameServer.Gateway/Player/PanelStats.cs").is_file():
        parser.error("Run this command from the repository root.")
    stats, stats_hash = read_table("Stats")
    panel, panel_hash = read_table("CharacterPanelStats")
    dependencies = {}
    for index, row in enumerate(panel):
        for field in ("StatsKeys1", "StatsKeys2", "StatsKeys3"):
            for key in row[field]:
                if not isinstance(key, int) or not 0 <= key < len(stats):
                    raise ValueError(f"Invalid Stats reference in panel row {index}: {key}")
                dependencies.setdefault(key, []).append({
                    "row": index, "id": row["Id"], "text": row["Text"],
                    "tab": row["CharacterPanelTabsKey"], "field": field,
                })

    # Authored review states are independent of literal matches and historical native certificates.
    ledger_bytes = args.ledger.read_bytes()
    ledger_text = ledger_bytes.decode("utf-8-sig")
    ledger = {}
    for name, status, record in re.findall(
            r"^\| `([^`]+)` \| (pending|partial|complete) \| (.+) \|\r?$", ledger_text, re.M):
        if name in ledger:
            raise ValueError(f"Duplicate ledger stat: {name}")
        ledger[name] = {"status": status, "record": record}
    target = {stats[key]["Id"] for key in dependencies if stats[key]["IsVirtual"]}
    if set(ledger) != target:
        raise ValueError(f"Ledger does not match the virtual target. Missing: {sorted(target - set(ledger))}; "
                         f"unexpected: {sorted(set(ledger) - target)}")

    # Index exact literals only: interpolation and numeric keys require a manual trace.
    # A mention in a test or source file does not certify a calculation or its lifecycle.
    tracked = git("ls-files", "-z").split("\0")
    literals = {}
    documents = []
    for name in tracked:
        path = Path(name)
        if not path.is_file():
            continue
        if path.suffix.lower() == ".md":
            raw = path.read_bytes()
            text = raw.decode("utf-8-sig")
            documents.append({
                "path": name, "sha256": hashlib.sha256(raw).hexdigest(),
                "line_count": len(text.splitlines()),
                "headings": re.findall(r"^#{1,3} .+", text, re.M),
                "panel_references": [
                    {"line": i, "text": line}
                    for i, line in enumerate(text.splitlines(), 1)
                    if re.search(r"character.panel|issue\s*#?\s*75|357|387", line, re.I)
                ],
            })
        if path.suffix == ".cs" and name.startswith(("src/", "tests/")):
            for line_number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
                for literal in set(re.findall(r'"([^"\r\n]+)"', line)):
                    literals.setdefault(literal, []).append({"path": name, "line": line_number})

    records = []
    for key in sorted(dependencies):
        stat = stats[key]
        records.append({
            "stat": stat["Id"], "wire_key": key + 1, "virtual": stat["IsVirtual"],
            "rows": dependencies[key], "exact_literal_candidates": literals.get(stat["Id"], []),
            "review_status": ledger[stat["Id"]]["status"] if stat["IsVirtual"] else "unreviewed",
            "review_record": ledger[stat["Id"]]["record"] if stat["IsVirtual"] else None,
        })
    counts = {"panel_rows": len(panel), "unique_stats": len(records),
              "virtual_stats": sum(row["virtual"] for row in records),
              "tracked_markdown_files": len(documents)}
    report = {
        "head": git("rev-parse", "HEAD"), "working_tree": git("status", "--short"),
        "table_sha256": {"Stats": stats_hash, "CharacterPanelStats": panel_hash},
        "counts": counts,
        "authored_review": {"path": args.ledger.as_posix(),
                            "sha256": hashlib.sha256(ledger_bytes).hexdigest(),
                            "counts": dict(Counter(row["status"] for row in ledger.values()))},
        "scope": "Inventory only. Candidates are navigation aids, not implementation or validation evidence. "
                 "No match does not prove absence; interpolated names and numeric keys need manual tracing. "
                 "Virtual review states are copied from the authored ledger, never inferred from source matches. "
                 "Supporting nonvirtual dependencies remain unreviewed here.",
        "stats": records, "documents": documents,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(counts))
    print("Authored review:", report["authored_review"]["counts"])
    print("Report:", args.output)


if __name__ == "__main__":
    main()
