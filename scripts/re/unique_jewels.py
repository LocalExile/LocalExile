"""Generate src/GameServer.Gateway/Item/UniqueJewelCatalogue.cs from Path of Building's unique jewel data.

Path of Building is a read-only third-party reference under Tools/PathOfBuilding (gitignored). Its
src/Export/Uniques/jewel.lua has the mod HASH16 names, while src/Data/Uniques/jewel.lua has the human-readable
effect text used to classify the jewels. The generated C# file is committed so the Gateway does not need the tool.

Usage (repository root, with Tools/PathOfBuilding present):

    python scripts/re/unique_jewels.py
"""
import json
import re

EXPORT = "Tools/PathOfBuilding/src/Export/Uniques/jewel.lua"
TEXT = "Tools/PathOfBuilding/src/Data/Uniques/jewel.lua"
OUT = "src/GameServer.Gateway/Item/UniqueJewelCatalogue.cs"
VISUALS_OUT = "src/GameServer.Gateway/Item/UniqueJewelVisuals.cs"

META = re.compile(r"^(Source|League|Radius|Limited to|Variant|Requires Level|Upgrade|DropLevel|Level|"
                  r"Crafted|Implicits|Has Alt Variant|Corrupted)")
VARIANT = re.compile(r"^\{variant:([^}]*)\}(.+)$")
MOD = re.compile(r"^([A-Za-z0-9_]+)(?:\[(-?\d+),(-?\d+)\])?$")


def block_lines(path):
    text = open(path, encoding="utf-8").read()
    for block in re.findall(r"\[\[(.*?)\]\]", text, re.S):
        lines = [line.strip() for line in block.strip().splitlines() if line.strip()]
        if not lines:
            continue
        if lines[0] == "Item Class: Jewels":
            if len(lines) < 4:
                continue
            yield lines[2], lines[3], lines[4:]
        else:
            yield lines[0], lines[1] if len(lines) > 1 else "", lines[2:]


def classify(base, meta, mods):
    if "Cluster Jewel" in base:
        return "Cluster"
    if "Eye Jewel" in base:
        return "Abyss"
    if base == "Timeless Jewel":
        return "Timeless"
    if any(line.startswith("Radius:") for line in meta) or "in radius" in mods or "in a radius" in mods:
        return "Radius"
    if not mods:
        return "Special"
    if re.search(r"\b(if|unless|while|when|recently|on .*kill|on hit|with \d+|with .* equipped|for each|"
                 r"counts as|transform|replaced|replace |until|primordial|jewel socket passive skills containing)\b", mods) \
            or re.search(r"\bper (?!second|minute)\w", mods):
        return "Conditional"
    return "Global"


def main():
    rows = json.load(open("data/tables/Mods.json", encoding="utf-8"))
    known_mods = {row["Id"] for row in rows}
    words = json.load(open("data/tables/Words.json", encoding="utf-8"))
    layout = json.load(open("data/tables/UniqueStashLayout.json", encoding="utf-8"))
    visuals = json.load(open("data/tables/ItemVisualIdentity.json", encoding="utf-8"))
    base_rows = json.load(open("data/tables/BaseItemTypes.json", encoding="utf-8"))
    drop_levels = {row["Name"]: row.get("DropLevel", 1) for row in base_rows}

    visual_by_name = {}
    for row in layout:
        if row.get("UniqueStashTypesKey") != 19:
            continue
        name = words[row["WordsKey"]]["Text"]
        if name in visual_by_name and (row.get("IsAlternateArt") or visual_by_name[name][1]):
            continue
        visual_by_name[name] = (visuals[row["ItemVisualIdentityKey"]]["HASH16"], bool(row.get("IsAlternateArt")))

    # Human-readable effects, by name, for classification only.
    texts = {}
    for name, _base, rest in block_lines(TEXT):
        meta, effects = [], []
        for line in rest:
            if META.match(line):
                meta.append(line)
            else:
                effects.append(re.sub(r"^\{variant:\d+\}", "", line).strip())
        texts[name] = (meta, "\n".join(effects).lower())

    entries = []
    for name, base, rest in block_lines(EXPORT):
        meta, mod_lines = [], []
        for line in rest:
            if META.match(line) or (line.startswith("{variant:") and "Corrupted" in line):
                meta.append(line)
            else:
                mod_lines.append(line)
        variants = [line.split(":", 1)[1].strip() for line in meta if line.startswith("Variant:")]
        chosen = len(variants) if variants else 0
        mods = []
        for line in mod_lines:
            match = VARIANT.match(line)
            allowed = None
            if match:
                allowed = [int(value) for value in match.group(1).split(",") if value.strip().isdigit()]
                line = match.group(2).strip()
            match = MOD.match(line)
            if not match:
                continue
            mod, low, high = match.group(1), match.group(2), match.group(3)
            if allowed and chosen not in allowed:
                continue
            if mod in known_mods:
                mods.append((mod, int(high) if high is not None else None))
        if not mods:
            continue
        human_meta, human_text = texts.get(name, ([], ""))
        if classify(base, human_meta, human_text) != "Global":
            continue
        level = next((int(re.sub(r"\D", "", line)) for line in meta if line.startswith("Requires Level")), None)
        level = level if level is not None else drop_levels.get(base, 1)
        entries.append(dict(name=name, base=base, corrupted=any("Corrupted" in line for line in meta),
                            level=level, mods=[mod for mod, _ in mods], values=[value for _, value in mods]))

    # Item sources whose effects are conditional or need a mechanic the server does not model yet. They are
    # creatable and their stats reach the item/stat path, but the listed mechanic stays partial/inert.
    special = [
        dict(name="Rational Doctrine", base="Cobalt Jewel", corrupted=False, level=1,
             mods=["ConsecratedGroundStationarySTRHighestUnique__1",
                   "ProfaneGroundCriticalStrikeINTHighestUnique__1",
                   "ConsecratedGroundLingersUnique__1",
                   "ProfaneGroundLingersUnique__1"],
             values=[None, None, None, None]),
    ]

    def quoted(value):
        return json.dumps(value, ensure_ascii=False)

    def values(values):
        return "[" + ", ".join("null" if value is None else str(value) for value in values) + "]"

    lines = [
        "// Generated from Path of Building src/Export/Uniques/jewel.lua for the target 3.29.3b data.",
        "// Do not edit by hand; regenerate with scripts/re/unique_jewels.py (a read-only third-party reference).",
        "// Global jewels have no radius and no conditional mechanic; Special jewels have an item source but a",
        "// mechanic that is not modelled yet.",
        "",
        "/// <summary>The non-radius unique jewels: base type and mod HASH16s. Global entries are unconditional;",
        "/// Special entries are item sources whose mechanic remains partial/inert. The unique art of every jewel is",
        "/// in <see cref=\"UniqueJewelVisuals\"/>.</summary>",
        "static class UniqueJewelCatalogue",
        "{",
        "    public sealed record Entry(string Name, string Base, bool Corrupted, int Level,",
        "        string[] Mods, int?[] Values);",
        "",
        "    public static readonly Entry[] Global =",
        "    [",
    ]
    for entry in sorted(entries, key=lambda item: item["name"].lower()):
        mods = "[" + ", ".join(quoted(mod) for mod in entry["mods"]) + "]"
        lines.append(f"        new({quoted(entry['name'])}, {quoted(entry['base'])}, "
                     f"{str(entry['corrupted']).lower()}, {entry['level']}, {mods}, {values(entry['values'])}),")
    lines += [
        "    ];",
        "",
        "    public static readonly Entry[] Special =",
        "    [",
    ]
    for entry in special:
        mods = "[" + ", ".join(quoted(mod) for mod in entry["mods"]) + "]"
        lines.append(f"        new({quoted(entry['name'])}, {quoted(entry['base'])}, "
                     f"{str(entry['corrupted']).lower()}, {entry['level']}, {mods}, {values(entry['values'])}),")
    lines += [
        "    ];",
        "",
        "    public static Entry? Find(string name) =>",
        "        Global.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))",
        "        ?? Special.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));",
        "",
        "    public static string[] Names =>",
        "        [.. Global.Concat(Special).Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase)];",
        "}",
        "",
    ]
    open(OUT, "w", encoding="utf-8").write("\n".join(lines))
    print(f"wrote {OUT}: {len(entries)} global unique jewels")

    # The unique art of every unique jewel (not only the Global ones): Voices and the cluster jewels, the radius
    # jewels and the timeless ones all get their own icon from here once their mechanic exists.
    visual_lines = [
        "// Generated from the client's UniqueStashLayout, Words and ItemVisualIdentity for the target 3.29.3b data.",
        "// Do not edit by hand; regenerate with scripts/re/unique_jewels.py.",
        "",
        "/// <summary>The unique art of every unique jewel the stash layout lists, by name: the ItemVisualIdentity",
        "/// HASH16 the client draws for the item. A jewel whose name is missing has no art of its own.</summary>",
        "static class UniqueJewelVisuals",
        "{",
        "    static readonly Dictionary<string, ushort> ByName = new(StringComparer.OrdinalIgnoreCase)",
        "    {",
    ]
    for name in sorted(visual_by_name, key=str.lower):
        visual_lines.append(f"        [{quoted(name)}] = {visual_by_name[name][0]},")
    visual_lines += [
        "    };",
        "",
        "    public static ushort? Of(string name) =>",
        "        ByName.TryGetValue(name, out ushort visual) && visual != 0 ? visual : null;",
        "}",
        "",
    ]
    open(VISUALS_OUT, "w", encoding="utf-8").write("\n".join(visual_lines))
    print(f"wrote {VISUALS_OUT}: {len(visual_by_name)} unique jewel visuals")


if __name__ == "__main__":
    main()
