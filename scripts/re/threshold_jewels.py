"""Generate src/GameServer.Gateway/Item/ThresholdJewelCatalogue.cs from Path of Building's threshold jewels.

Path of Building is a read-only third-party reference under Tools/PathOfBuilding (gitignored). Its
src/Export/Uniques/jewel.lua has the mod HASH16 names for every threshold jewel. The generated C# file is
committed so the Gateway does not need the tool.

The threshold jewels have local `..._with_40_<attribute>_in_radius` mods. The client applies those mods to
the passive tree; the server's flat stat model only reads the unconditional global mods, so the catalogue
records the whole item and ReadConversion returns null for it.

Usage (repository root, with Tools/PathOfBuilding present):

    python scripts/re/threshold_jewels.py
"""
import json
import re

EXPORT = "Tools/PathOfBuilding/src/Export/Uniques/jewel.lua"
MODS = "data/tables/Mods.json"
OUT = "src/GameServer.Gateway/Item/ThresholdJewelCatalogue.cs"

META = re.compile(
    r"^(Source|League|Radius|Limited to|Variant|Requires Level|Upgrade|DropLevel|Level|Crafted|"
    r"Implicits|Has Alt Variant|Corrupted)")
VARIANT = re.compile(r"^\{variant:([^}]*)\}(.+)$")
MOD = re.compile(r"^([A-Za-z0-9_]+)(?:\[(-?\d+),(-?\d+)\])?$")

THRESHOLD_SECTION = ("-- Jewel: Threshold", "-- Jewel: Corrupted")
ADDITIONAL = ("Divine Inferno", "Endless Misery")
EXCLUDED = ("Ancestral Vision", "Pitch Darkness", "Sudden Ignition")
# PoB leaves The Vigil's "(8-15)% increased Armour" as text because its mod row is not in the export. The
# client's 10-15% unique armour row is the closest available global stat.
MANUAL_GLOBALS = {"The Vigil": ["IncreasedPhysicalDamageReductionRatingPercentUnique__1"]}
EXPECTED = 33


def blocks(text):
    for block in re.findall(r"\[\[(.*?)\]\]", text, re.S):
        lines = [line.strip() for line in block.strip().splitlines() if line.strip()]
        if lines:
            yield lines[0], lines[1] if len(lines) > 1 else "", lines[2:]


def choose_variant(labels):
    """The current variant, or the first non-Pre variant (Might and Influence's weapon variants)."""
    current = [index for index, label in enumerate(labels, 1) if label.lower() == "current"]
    if current:
        return current[-1]
    non_pre = [index for index, label in enumerate(labels, 1) if not label.lower().startswith("pre ")]
    return non_pre[0] if non_pre else 1


def main():
    text = open(EXPORT, encoding="utf-8").read()
    start = text.index(THRESHOLD_SECTION[0])
    end = text.index(THRESHOLD_SECTION[1])
    section = text[start:end]
    threshold_names = {name for name, _base, _rest in blocks(section)} - set(EXCLUDED)
    threshold_names.update(ADDITIONAL)

    mod_rows = {row["Id"]: row for row in json.load(open(MODS, encoding="utf-8"))}
    stat_names = [row["Id"] for row in json.load(open("data/tables/Stats.json", encoding="utf-8"))]

    def stats_of(mod_id):
        row = mod_rows.get(mod_id)
        if row is None:
            return []
        return [stat_names[row[f"StatsKey{index}"]]
                for index in range(1, 9)
                if row.get(f"StatsKey{index}") is not None
                and 0 <= row[f"StatsKey{index}"] < len(stat_names)]

    def is_threshold(mod_id):
        return any(stat == "local_jewel_effect_base_radius"
                   or (stat.startswith("local_") and "_in_radius" in stat)
                   for stat in stats_of(mod_id))

    entries = []
    seen = set()
    for name, base, rest in blocks(text):
        if name not in threshold_names:
            continue
        meta = [line for line in rest if META.match(line)]
        labels = [line.split(":", 1)[1].strip() for line in meta if line.startswith("Variant:")]
        chosen = choose_variant(labels)
        mod_ids = []
        for line in rest:
            if META.match(line):
                continue
            allowed = None
            match = VARIANT.match(line)
            if match:
                allowed = [int(value) for value in match.group(1).split(",") if value.strip().isdigit()]
                line = match.group(2).strip()
            match = MOD.match(line)
            if not match:
                continue
            mod_id = match.group(1)
            if allowed and chosen not in allowed:
                continue
            if mod_id in mod_rows and mod_id not in mod_ids:
                mod_ids.append(mod_id)
        for mod_id in MANUAL_GLOBALS.get(name, []):
            if mod_id in mod_rows and mod_id not in mod_ids:
                mod_ids.insert(0, mod_id)
        if not mod_ids:
            continue
        key = (name, base)
        if key in seen:
            continue
        seen.add(key)
        limit = next((int(re.sub(r"\D", "", line)) for line in meta if line.startswith("Limited to")), 0)
        globals_ = [mod_id for mod_id in mod_ids if not is_threshold(mod_id)]
        thresholds = [mod_id for mod_id in mod_ids if is_threshold(mod_id)]
        entries.append((name, base, limit, globals_, thresholds))
        print(f"{name} ({base}): {len(globals_)} global, {len(thresholds)} threshold")

    if len(entries) != EXPECTED:
        raise SystemExit(f"expected {EXPECTED} threshold jewels, found {len(entries)}")

    def quoted(value):
        return json.dumps(value, ensure_ascii=False)

    def array(values):
        return "[" + ", ".join(quoted(value) for value in values) + "]"

    lines = [
        "// Generated from Path of Building src/Export/Uniques/jewel.lua for the target 3.29.3b data.",
        "// Do not edit by hand; regenerate with scripts/re/threshold_jewels.py.",
        "",
        "/// <summary>The 33 threshold jewels: item name, base, limits and the client mods. The mods are",
        "/// <c>local_unique_jewel_..._with_40_&lt;attribute&gt;_in_radius</c> (or the 50-attribute variants); the",
        "/// client applies them to the passive tree, while the server's flat stat model only consumes the",
        "/// unconditional global mods. ReadConversion therefore returns null for these items.</summary>",
        "static class ThresholdJewelCatalogue",
        "{",
        "    public static readonly RadiusJewelCatalogue.Entry[] All =",
        "    [",
    ]
    for name, base, limit, globals_, thresholds in entries:
        if thresholds:
            conversion = quoted(thresholds[0])
            extra = thresholds[1:]
            line = (f"        new({quoted(name)}, {quoted(base)}, {array(globals_)}, {conversion}, "
                    f"{limit}, 1, false, 1200")
        else:
            conversion = quoted(globals_[0])
            extra = globals_[1:]
            line = (f"        new({quoted(name)}, {quoted(base)}, [], {conversion}, "
                    f"{limit}, 1, false, 1200")
        if extra:
            line += f", null, {array(extra)}"
        line += "),"
        lines.append(line)
    lines += [
        "    ];",
        "}",
        "",
    ]
    open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
    print(f"wrote {OUT}: {len(entries)} threshold jewels")


if __name__ == "__main__":
    main()
