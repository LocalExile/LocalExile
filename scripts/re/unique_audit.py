"""Audit the unique-item catalogue against the extracted game data and the Gateway's stat consumers.

The seed (`src/GameServer.Gateway/Item/UniqueItemSeed.cs`) is the full unique catalogue, but a definition only says
which Mods.dat rows a unique carries. Whether those rows *do* anything on this server depends on the stats they carry
and on code that reads each stat id. This script makes that gap countable instead of a guess:

* every seed definition resolves to a base type, a unique name (Words + UniqueStashLayout) and its Mods.dat rows;
* every stat id is classified against Stats.dat and against the string literals in the C# sources
  (`--source src --source Modules ...`), which is where a consumer names the stat it reads;
* stats are grouped into the mechanics that need more than a passive number (sockets, triggers, keystones, buffs,
  charges, flasks, minions, conversion, conditions) and mods that grant a skill are flagged separately.

A literal is an approximation of a consumer, not proof: a stat can be folded onto a plain name
(`ItemModStats.Sum`'s `statExists`), read through a table, or be display-only in the client. The report therefore
says "no literal found" and never "not implemented". Only the real client and the logs settle that.

Usage (repository root):

    python scripts/re/unique_audit.py
    python scripts/re/unique_audit.py --out Tools/re/out/unique-audit.md --json Tools/re/out/unique-audit.json

The default output goes under `Tools/re/out/` (git-ignored). Nothing outside the seed and the data tables is written.
"""
import argparse
import json
import os
import pathlib
import re
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "dev"))
import clientdb  # noqa: E402  (scripts/dev/clientdb.py: the client tables in data/client.db)

ROOT = pathlib.Path(".")
SEED = ROOT / "src/GameServer.Gateway/Item/UniqueItemSeed.cs"
DEFAULT_OUT = ROOT / "Tools/re/out/unique-audit.md"
DEFAULT_JSON = ROOT / "Tools/re/out/unique-audit.json"

# Mechanics that are more than a passive stat sum. Each tag is a regular expression on a stat id; a unique carries the
# tag when any of its mods' stats matches.
MECHANICS = {
    "sockets": r"socket|abyss",
    "trigger": r"trigger|on_kill|on_hit|on_crit|when_you|on_death|on_block|on_equip",
    "keystone": r"keystone_|allocates_",
    "buff": r"_buff|buff_",
    "charges": r"charge",
    "flask": r"flask",
    "minion": r"minion|summon",
    "conversion": r"converted_to|gain_.*_as_|_as_extra",
    "condition": r"_if_|_while_|_per_|_with_|_against_|_when_",
    "curse": r"curse",
    "aura": r"aura",
    "skill_gem": r"socketed_gem|gem_level|support",
}

# A quoted C# string that looks like a Stats.dat id. The sources are scanned once and the set is reused for every stat.
LITERAL = re.compile(r'"([A-Za-z0-9_+%\-]{3,})"')
# An interpolated string builds an id at run time (`$"local_minimum_added_{slug}_damage"`). Its holes become a wildcard
# so the ids it can produce count as consumed; the literal parts must stay at least as long as a stat id's own words.
HOLE = re.compile(r"\{[^{}]*\}")
SEED_ROW = re.compile(r'^\s*new\(("(?:[^"\\]|\\.)*"),\s*("(?:[^"\\]|\\.)*")(.*)\),\s*$')


def seed_definitions():
    """Every definition in the generated seed, as the fields the audit needs."""
    rows = []
    for line in SEED.read_text(encoding="utf-8").splitlines():
        match = SEED_ROW.match(line)
        if not match:
            continue
        name = json.loads(match.group(1))
        base = json.loads(match.group(2))
        rest = match.group(3)
        mods = json.loads(re.match(r",\s*(\[[^\]]*\])", rest).group(1))
        implicits = re.search(r"Implicits: (\[[^\]]*\])", rest)
        rows.append({
            "name": name,
            "base": base,
            "mods": mods,
            "implicits": json.loads(implicits.group(1)) if implicits else None,
            "corrupted": "Corrupted: true" in rest,
            "ranges": "Ranges: [" in rest,
            "influences": "Influences:" in rest,
        })
    return rows


def source_literals(folders):
    """Every quoted string in the C# sources, as the candidate consumers of a stat id.

    Returns the plain literals and, separately, regexes for the ids an interpolated string can build. A literal that
    contains a hole (`local_minimum_added_{slug}_damage`) is not a stat id itself, but the ids it produces are
    consumed by that code."""
    found = set()
    patterns = set()
    for folder in folders:
        for path in pathlib.Path(folder).rglob("*.cs"):
            if any(part in { "obj", "bin" } for part in path.parts):
                continue
            try:
                text = path.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            found.update(LITERAL.findall(text))
            for match in re.finditer(r'\$"((?:[^"\\]|\\.)*)"', text):
                value = match.group(1)
                if not HOLE.search(value):
                    continue
                parts = [re.escape(part) for part in HOLE.split(value)]
                if any(len(part) >= 4 for part in parts):
                    patterns.add(re.compile("^" + "[A-Za-z0-9_+%\\-]+".join(parts) + "$"))
    return found, patterns


def consumed(stat, literals, patterns):
    return stat in literals or any(pattern.match(stat) for pattern in patterns)


def tags(stat_ids):
    result = []
    for tag, pattern in MECHANICS.items():
        if any(re.search(pattern, stat) for stat in stat_ids):
            result.append(tag)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--source", action="append", default=None,
                        help="a C# source folder to scan for stat literals (repeatable; default src and Modules)")
    parser.add_argument("--out", type=pathlib.Path, default=DEFAULT_OUT)
    parser.add_argument("--json", type=pathlib.Path, default=DEFAULT_JSON)
    parser.add_argument("--top", type=int, default=40, help="how many uncovered stats to list")
    args = parser.parse_args()

    mods = {row["Id"]: row for row in clientdb.table("Mods")}
    # StatsKey1..8 are indexes into Stats.dat, not ids (ModCatalog reads them that way).
    stat_rows = clientdb.table("Stats")
    stats = {row["Id"]: row for row in stat_rows}

    def stat_id(row, i):
        index = row.get(f"StatsKey{i}")
        return stat_rows[index]["Id"] if isinstance(index, int) and 0 <= index < len(stat_rows) else None
    bases = {row["Id"]: row for row in clientdb.table("BaseItemTypes")}
    words = {(row["Text"] or "").replace("\u2019", "'").lower()
             for row in clientdb.table("Words")
             if row["Wordlist"] == 6 and row["Text"]}
    # Names the seed spells the way PoB does while Words uses the client's spelling (UniqueCatalog.NameKey).
    spellings = {"Rigwald's Charge": "Rigvald's Charge", "The Broken Elegy": "Broken Elegy",
                 "The Goddess Unleashed": "The GoddessUnleashed"}

    def name_key(name):
        key = name.replace("\u2019", "'").replace("\u2018", "'").replace("\u02bc", "'")
        return spellings.get(key, key).lower()
    literals, patterns = source_literals(args.source or ["src", "Modules"])

    definitions = seed_definitions()
    missing_base = [d["name"] for d in definitions if d["base"] not in bases]
    missing_name = [d["name"] for d in definitions if name_key(d["name"]) not in words]
    missing_mods = sorted({mod for d in definitions for mod in d["mods"] + (d["implicits"] or []) if mod not in mods})
    granted = 0
    per_unique = []
    uncovered_by_unique = Counter()
    covered_by_unique = Counter()
    category = Counter()
    for d in definitions:
        stat_ids = []
        granted_skill = False
        for mod_id in d["mods"] + (d["implicits"] or []):
            row = mods.get(mod_id)
            if not row:
                continue
            if row.get("GrantedEffectsPerLevelKeys"):
                granted_skill = True
            for i in range(1, 9):
                if (value := stat_id(row, i)) is not None:
                    stat_ids.append(value)
        if granted_skill:
            granted += 1
        known = [s for s in stat_ids if s in stats]
        covered = [s for s in known if consumed(s, literals, patterns)]
        uncovered = [s for s in known if not consumed(s, literals, patterns)]
        unknown = [s for s in stat_ids if s not in stats]
        for tag in tags(stat_ids):
            category[tag] += 1
        for stat in uncovered:
            uncovered_by_unique[stat] += 1
        for stat in covered:
            covered_by_unique[stat] += 1
        per_unique.append({
            "name": d["name"], "base": d["base"], "mods": len(d["mods"]),
            "stats": len(stat_ids), "covered": len(covered), "uncovered": len(uncovered),
            "uncovered_ids": uncovered, "unknown": len(unknown), "tags": tags(stat_ids),
            "corrupted": d["corrupted"], "granted_skill": granted_skill,
        })

    fully = sum(1 for row in per_unique if row["stats"] and not row["uncovered"] and not row["unknown"])
    partial = sum(1 for row in per_unique if row["stats"] and (row["uncovered"] or row["unknown"]) and row["covered"])
    none = sum(1 for row in per_unique if row["stats"] and not row["covered"])
    statless = sum(1 for row in per_unique if not row["stats"])
    summary = {
        "definitions": len(definitions),
        "bases_missing": len(missing_base),
        "names_missing": len(missing_name),
        "mod_ids_missing": len(missing_mods),
        "uniques_granting_a_skill": granted,
        "unique_fully_covered": fully,
        "unique_partially_covered": partial,
        "unique_no_covered_stat": none,
        "unique_without_stats": statless,
        "mechanics": dict(sorted(category.items())),
        "names_missing_list": missing_name,
        "bases_missing_list": missing_base,
        "uncovered_stats": uncovered_by_unique.most_common(args.top),
    }

    lines = [
        "# Unique mechanic coverage audit",
        "",
        f"Seed definitions: **{summary['definitions']}**. Generated from `{SEED.as_posix()}` and the client tables (data/client.db);",
        "run `python scripts/re/unique_audit.py` again after the seed or the data changes.",
        "",
        "A stat is *covered* when its id appears as a string literal in the scanned C# sources, or matches the ids an",
        "interpolated string can build (for example `local_minimum_added_{slug}_damage`). That is an approximation of a",
        "consumer, not proof: table reads, folded conditional names and client-only display all escape it.",
        "",
        "## Totals",
        "",
        "| Measure | Count |",
        "|---|---|",
        f"| Seed definitions | {summary['definitions']} |",
        f"| Base type missing from BaseItemTypes | {summary['bases_missing']} |",
        f"| Name missing from Words/UniqueStashLayout | {summary['names_missing']} |",
        f"| Mod id missing from Mods.dat | {summary['mod_ids_missing']} |",
        f"| Uniques whose mods grant a skill | {summary['uniques_granting_a_skill']} |",
        f"| Every stat has a literal | {fully} |",
        f"| Some stats have a literal | {partial} |",
        f"| No stat has a literal | {none} |",
        f"| No stat at all | {statless} |",
        "",
        "## Mechanics present in the catalogue",
        "",
        "| Mechanic | Uniques carrying a matching stat |",
        "|---|---|",
    ]
    lines += [f"| {tag} | {count} |" for tag, count in sorted(category.items(), key=lambda kv: -kv[1])]
    lines += ["", "## Stats without a literal consumer (top " + str(args.top) + ")", "",
              "| Stat | Uniques carrying it |", "|---|---|"]
    lines += [f"| `{stat}` | {count} |" for stat, count in summary["uncovered_stats"]]
    lines += ["", "## Uniques with an uncovered stat", "",
              "| Unique | Stats | Uncovered | Uncovered ids |", "|---|---|---|---|"]
    for row in per_unique:
        if row["uncovered"] and len(lines) < 4000:
            lines.append(f"| {row['name']} | {row['stats']} | {row['uncovered']} | "
                         f"{', '.join('`' + s + '`' for s in row['uncovered_ids'][:6])} |")
    if missing_base or missing_name or missing_mods:
        lines += ["", "## Data gaps", ""]
        for label, values in (("Base type", missing_base), ("Name", missing_name), ("Mod id", missing_mods)):
            if values:
                lines.append(f"- {label} missing: " + ", ".join(f"`{v}`" for v in values[:50]))
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    args.json.parent.mkdir(parents=True, exist_ok=True)
    args.json.write_text(json.dumps({"summary": summary, "uniques": per_unique}, indent=1), encoding="utf-8")
    print(json.dumps(summary, indent=1))
    print("wrote", args.out, args.json)


if __name__ == "__main__":
    main()
