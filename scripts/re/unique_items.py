"""Generate src/GameServer.Gateway/UniqueItemSeed.cs from Path of Building's unique item export.

Path of Building is a read-only third-party reference under Tools/PathOfBuilding (gitignored). Its
src/Export/Uniques/*.lua files list the unique name, base type and Mods.dat ids; src/Data/Uniques/*.lua has the
human-readable text. The generated C# file is committed so the Gateway does not need the tool at runtime.

Usage (repository root, with Tools/PathOfBuilding present):

    python scripts/re/unique_items.py

The generated rows are the seed for the `catalog_entries` kind `unique_item`. The base type is resolved against the
extracted BaseItemTypes table; malformed PoB blocks without a resolvable base are skipped and reported.
"""
import json
import pathlib
import re
from collections import Counter, defaultdict

ROOT = pathlib.Path(".")
EXPORT = ROOT / "Tools/PathOfBuilding/src/Export/Uniques"
OUT = ROOT / "src/GameServer.Gateway/UniqueItemSeed.cs"

META = re.compile(
    r"^(Source|League|Radius|Limited to|Variant|Requires Level|Upgrade|DropLevel|Level|Crafted|Implicits|"
    r"Has Alt Variant|Corrupted)"
)
VARIANT = re.compile(r"^\{variant:([^}]*)\}(.+)$")
VARIANT_PREFIX = re.compile(r"^\{variant:[^}]*\}")
MOD = re.compile(r"^([A-Za-z0-9_]+)(?:\[(-?\d+),(-?\d+)\])?$")


def blocks(path):
    text = path.read_text(encoding="utf-8")
    for block in re.findall(r"\[\[(.*?)\]\]", text, re.S):
        lines = [line.strip() for line in block.strip().splitlines() if line.strip()]
        if len(lines) < 2:
            continue
        yield lines[0], lines[1], lines[2:]


def main():
    base = json.load(open(ROOT / "data/tables/BaseItemTypes.json", encoding="utf-8"))
    mods = json.load(open(ROOT / "data/tables/Mods.json", encoding="utf-8"))
    known_mods = {row["Id"] for row in mods}
    by_name = defaultdict(list)
    for row in base:
        by_name[row["Name"]].append(row)

    rows = []
    skipped = []
    for path in sorted(EXPORT.glob("*.lua")):
        for name, raw_base, rest in blocks(path):
            base_name = VARIANT_PREFIX.sub("", raw_base).strip()

            meta, mod_lines = [], []
            implicits = 0
            for line in rest:
                if line.startswith("Implicits:"):
                    implicits = int(line.split(":", 1)[1].strip() or 0)
                    continue
                if META.match(line):
                    meta.append(line)
                    continue
                mod_lines.append(line)

            variants = [line.split(":", 1)[1].strip() for line in meta if line.startswith("Variant:")]
            chosen = len(variants) if variants else 0

            mod_ids = []
            for line in mod_lines[implicits:]:
                match = VARIANT.match(line)
                allowed = None
                if match:
                    allowed = [int(value) for value in match.group(1).split(",") if value.strip().isdigit()]
                    line = match.group(2).strip()
                match = MOD.match(line)
                if not match:
                    continue
                mod_id = match.group(1)
                if allowed and chosen not in allowed:
                    continue
                if mod_id in known_mods:
                    mod_ids.append(mod_id)

            level = next((int(re.sub(r"\D", "", line)) for line in meta if line.startswith("Requires Level")), None)
            candidates = by_name.get(base_name, [])
            if not candidates:
                # Some newer bases carry a parenthesised subtype in PoB but not in the client table.
                candidates = by_name.get(base_name.split(" (", 1)[0], [])
            if not candidates:
                skipped.append(f"{path.name}: {name} ({base_name})")
                continue
            # Prefer the ordinary base over a Royale/event duplicate with the same name.
            chosen_base = next((row for row in candidates if "Royale" not in row["Id"]), candidates[0])
            rows.append({
                "name": name,
                "base": chosen_base["Id"],
                "level": max(1, min(255, level if level is not None else chosen_base.get("DropLevel", 1))),
                "mods": mod_ids,
                "corrupted": any("Corrupted" in line for line in meta),
            })

    # Stable output; duplicate names (Stormblood, Grand Spectrum, Combat Focus) keep every base variant.
    rows.sort(key=lambda row: (row["name"].lower(), row["base"].lower()))
    names = Counter(row["name"] for row in rows)
    lines = [
        "// Generated from Path of Building src/Export/Uniques/*.lua for the target 3.29.3b data.",
        "// Do not edit by hand; regenerate with scripts/re/unique_items.py (a read-only third-party reference).",
        "",
        "/// <summary>The full unique-item seed: name, base type, Mods.dat ids and item level. The catalogue table",
        "/// keeps the runtime rows; this array is the fresh-database fallback.</summary>",
        "static class UniqueItemSeed",
        "{",
        "    public static readonly UniqueCatalog.Definition[] All =",
        "    [",
    ]
    for row in rows:
        mods = "[" + ", ".join(json.dumps(mod, ensure_ascii=False) for mod in row["mods"]) + "]"
        corrupted = ", true" if row["corrupted"] else ""
        lines.append(f'        new({json.dumps(row["name"], ensure_ascii=False)}, {json.dumps(row["base"])}, {mods}, '
                     f'{row["level"]}{corrupted}),')
    lines += ["    ];", "}", ""]
    OUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"wrote {len(rows)} unique definitions to {OUT}")
    print("skipped:", len(skipped))
    for entry in skipped:
        print("  ", entry)
    print("duplicate names:", [(name, count) for name, count in names.items() if count > 1])


if __name__ == "__main__":
    main()
