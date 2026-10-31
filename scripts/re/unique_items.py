"""Generate src/GameServer.Gateway/Item/UniqueItemSeed.cs from Path of Building's unique item export.

Path of Building is a read-only third-party reference. Its src/Export/Uniques/*.lua files list the unique name, base
type and Mods.dat ids; src/Data/Uniques/*.lua has the human-readable text. The generated C# file is committed so the
Gateway does not need the tool at runtime.

Usage (repository root):

    python scripts/re/unique_items.py [--pob <Path of Building folder>]

The default folder is Tools/PathOfBuilding (gitignored). The generated rows are the seed for the `catalog_entries` kind
`unique_item`. The base type is resolved against the extracted BaseItemTypes table; malformed PoB blocks without a
resolvable base are skipped and reported.

A block in the export:

    Tulfall
    {variant:1,2}Tornado Wand         the base, one line per variant when it changed
    {variant:3}Opal Wand
    Variant: Pre 3.16.0               metadata, in no fixed order
    Variant: Current
    Implicits: 2                      the next two lines are implicits (each may belong to some variants only),
    {variant:1,2}ModId                the lines after them explicits
    {variant:3}ModId[10,15]           [min,max] per stat when PoB overrides Mods.dat's roll

The variants generated are the ones PoB shows by default (Classes/Item.lua): "Selected Variant", and with "Has Alt
Variant" each alternate slot's "Selected Alt Variant", else the last variant, which is the current one when the variants
are eras. A base line for no chosen variant means the newest line, the last.

The export ties an implicit to any row with that text, often an old base's, so its implicits are read against the client:
same-name bases (Two-Stone Ring, quivers) are told apart by them, and a unique keeps its base's implicits unless a line
names a unique's own row, the base has none, or the implicit is Eldritch.
"""
import argparse
import json
import pathlib
import re
from collections import Counter, defaultdict

ROOT = pathlib.Path(".")
POB = ROOT / "Tools/PathOfBuilding"
OUT = ROOT / "src/GameServer.Gateway/Item/UniqueItemSeed.cs"

# Each key with its colon: a bare prefix would also take mod ids (CorruptedMagicJewelModEffectUnique__1).
META = re.compile(
    r"^(?:(?:Source|League|Radius|Limited to|Variant|Selected Variant|Selected Alt Variant(?: \w+)?|Upgrade|DropLevel|"
    r"Level|LevelReq|Requires Level|Item Level|Crafted|Implicits|Has Alt Variant(?: \w+)?|Sockets|Energy Shield|Armour|"
    r"Evasion|Quality):|Corrupted$|Requires |This item can be anointed|[A-Z][a-z]+( [A-Za-z]+)* Item$)"
)
VARIANT = re.compile(r"^\{variant:([^}]*)\}(.+)$")
MOD = re.compile(r"^([A-Za-z0-9_]+)((?:\[-?\d+,-?\d+\])*)$")
RANGE = re.compile(r"\[(-?\d+),(-?\d+)\]")
# A line of example craft PoB keeps on the item; it is not the unique's own.
CRAFTED = "{crafted}"
FRACTURED = "{fractured}"
# The Eldritch ones have no state of their own: the client reads them from the implicit mods.
INFLUENCES = {f"{name} Item": name for name in ("Shaper", "Elder", "Crusader", "Redeemer", "Hunter", "Warlord")}
ELDRITCH = ("Searing Exarch Item", "Eater of Worlds Item")
UNIQUE_MOD, EXARCH_IMPLICIT, EATER_IMPLICIT = 3, 28, 29
ROLL = re.compile(r"\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)|(?<![A-Za-z])(\d+(?:\.\d+)?)")


NAME_SPELLINGS = {"Rigwald's Charge": "Rigvald's Charge", "The Broken Elegy": "Broken Elegy",
                  "The Goddess Unleashed": "The GoddessUnleashed"}
# Rolls the export gets wrong: Mods.dat's row and poedb agree against PoB's [min,max] (checked 2026-10-31).
STALE_ROLLS = {
    ("Bino's Kitchen Knife", "ChaosResistUniqueDagger8"),
    ("Cragfall", "StunDurationUniqueQuiver8"),
    ("Death Rush", "AdrenalineOnKillUnique__1"),
    ("Death's Opus", "LocalAddedPhysicalDamageUnique__17_"),
    ("Ezomyte Hold", "LocalIncreasedPhysicalDamageReductionRatingUniqueHelmetStr1"),
    ("Geofri's Legacy", "FireResistUniqueHelmetStrInt2"),
    ("Geofri's Legacy", "ColdResistUniqueHelmetStrInt2"),
    ("Geofri's Legacy", "LightningResistUniqueHelmetStrInt2"),
    ("Hrimburn", "AttackerTakesColdDamageGlovesDex1"),
    ("Martyr's Crown", "TakesDamageWhenAttackedUniqueIntHelmet1"),
    ("Ngamahu Tiki", "EvasionOnLowLifeUniqueAmulet4"),
    ("Replica Atziri's Acuity", "CriticalStrikeChanceUniqueGlovesStr3"),
    ("Replica Soul Taker", "LocalAddedPhysicalDamageOneHandAxe1"),
    ("Sadima's Touch", "ItemFoundRarityIncreaseUnique__7"),
    ("Sidhebreath", "MinionAddedColdDamageUnique__1"),
    ("Thirst for Horrors", "ChanceForEnemyToFleeOnBlockUniqueShieldDex4"),
    ("Thirst for Horrors", "IncreaseSocketedCurseGemLevelUniqueShieldDex4"),
    ("Voidheart", "AddedChaosDamageUnique__1"),
    ("Wall of Brambles", "IncreasedLifeUniqueBodyStr2"),
    ("Wall of Brambles", "PhysicalAttackDamageReducedUniqueBodyStr2"),
    ("Wall of Brambles", "PhysicalDamageTakenPercentToReflectUniqueBodyStr2"),
    ("Wildwrap", "FlaskManaRecoveryUniqueBodyDex7"),
    ("Xirgil's Crank", "StaffBlockPercentUnique__1"),
}
# Uniques that always drop corrupted, which the export does not mark on their current variant.
CORRUPTED = {"Reckless Defence", "The Adorned", "Voices"}


def blocks(path):
    text = path.read_text(encoding="utf-8")
    for block in re.findall(r"\[\[(.*?)\]\]", text, re.S):
        lines = [line.strip() for line in block.strip().splitlines() if line.strip()]
        # A block pasted from the game keeps its item text header (That Which Was Taken).
        lines = [line for line in lines if not line.startswith(("Item Class:", "Rarity:"))]
        if len(lines) < 2:
            continue
        yield lines[0], lines[1:]


def tagged(line):
    """A line's text and the variants it belongs to (None: every variant)."""
    match = VARIANT.match(line)
    if not match:
        return line, None
    return match.group(2).strip(), {int(value) for value in match.group(1).split(",") if value.strip().isdigit()}


def in_variant(line, chosen):
    text, allowed = tagged(line)
    return text if allowed is None or allowed.intersection(chosen) else None


def chosen_variants(meta):
    """The variants PoB shows by default, the main one first: as Item.lua, an unselected slot takes the last."""
    last = sum(1 for line in meta if line.startswith("Variant:"))

    def selected(key):
        line = next((line for line in meta if line.startswith(f"{key}:")), None)
        return int(line.split(":", 1)[1]) if line else last

    return [selected("Selected Variant")] + [selected(f"Selected Alt Variant{slot}") for slot in ("", " Two", " Three")
                                             if any(line.startswith(f"Has Alt Variant{slot}:") for line in meta)]


def split_block(lines, known_base):
    """The base lines, the metadata lines, and the implicit and explicit mod lines of a block. Metadata can come before
    the base (Gorgon's Gaze, Replica Gifts from Above, Echoes of Creation)."""
    start = next((i for i, line in enumerate(lines) if known_base(tagged(line)[0])), 0)
    count = start + 1
    while count < len(lines) and VARIANT.match(lines[count]) and known_base(tagged(lines[count])[0]):
        count += 1
    bases, rest = lines[start:count], lines[:start] + lines[count:]
    marker = next((i for i, line in enumerate(rest) if line.startswith("Implicits:")), None)
    if marker is None:
        meta = [line for line in rest if META.match(line)]
        return bases, meta, None, [line for line in rest if not META.match(line)]
    implicits = int(rest[marker].split(":", 1)[1].strip() or 0)
    # Metadata can follow the marker too, so the implicit lines are counted among the mod lines only.
    after = rest[marker + 1:]
    mods = [line for line in after if not META.match(line)]
    return bases, rest[:marker] + [line for line in after if META.match(line)], mods[:implicits], mods[implicits:]


def by_text(text, text_map, known_mods, generations):
    """A mod the export left as text: its ModTextMap candidates that are unique mods, the one whose roll matches when
    several qualify. None when the text cannot be tied to one row. ModTextMap writes a range (#) and a fixed number #."""
    rolls = [(float(m.group(1)), float(m.group(2))) if m.group(1) else (float(m.group(3)), float(m.group(3)))
             for m in ROLL.finditer(text)]
    key = ROLL.sub(lambda m: "(#)" if m.group(1) else "#", text).lower().lstrip("+")
    rows = [known_mods[i] for i in text_map.get(key, []) if i in known_mods and known_mods[i]["GenerationType"] in generations]
    def fits(row):
        bounds = [(row.get(f"Stat{i + 1}Min"), row.get(f"Stat{i + 1}Max")) for i in range(len(rolls))]
        return all(None not in pair and sorted(roll) == sorted(pair) for roll, pair in zip(rolls, bounds))
    matching = sorted((row for row in rows if fits(row)), key=lambda row: row["Id"])
    if matching and all(row["StatsKey1"] == matching[0]["StatsKey1"] for row in matching):
        return matching[0]["Id"], []
    if len(rows) == 1 and len(rolls) == 1:
        low, high = sorted(rolls[0])
        return (rows[0]["Id"], [[int(low), int(high)]]) if low == int(low) and high == int(high) else None
    return None


def resolve(lines, chosen, known_mods, text_map, unknown, flags, generations=(UNIQUE_MOD,), fractured=None):
    """The chosen variant's mods: (id, the [min, max] PoB gives per stat, empty for Mods.dat's own)."""
    result = []
    for line in lines:
        text = in_variant(line, chosen)
        if text is None or text.startswith(CRAFTED):
            continue
        if text in ("Corrupted", "Mirrored") or text in INFLUENCES:
            flags.add(text)
            continue
        if META.match(text):
            continue
        if fractured is not None and text.startswith(FRACTURED):
            fractured.add(len(result))
        text = text.removeprefix(FRACTURED)
        match = MOD.match(text)
        if match and match.group(1) in known_mods:
            result.append((match.group(1), [[int(low), int(high)] for low, high in RANGE.findall(match.group(2))]))
        elif (found := by_text(text, text_map, known_mods, generations)) is not None:
            result.append(found)
        else:
            unknown.append(text)
    return result


def effect(mod_rows, mods):
    """What mods do: each stat with its roll. Rows with the same effect are the same line on the item."""
    result = []
    for mod_id, ranges in mods:
        row = mod_rows[mod_id]
        for stat in range(8):
            if row.get(f"StatsKey{stat + 1}") is None:
                break
            low, high = ranges[stat] if stat < len(ranges) else (row.get(f"Stat{stat + 1}Min"), row.get(f"Stat{stat + 1}Max"))
            result.append((row[f"StatsKey{stat + 1}"], min(low, high), max(low, high)))
    return sorted(result)


def choose_base(found, implicits, mod_rows, base_mods):
    """The row of a base name: same-name rows differ by their implicits (Two-Stone Ring), then by age (the old quiver
    rows keep a low DropLevel). A Royale/event duplicate is never preferred."""
    rows = [row for row in found if "Royale" not in row["Id"]] or found
    wanted = effect(mod_rows, implicits)
    same = [row for row in rows if effect(mod_rows, base_mods(row)) == wanted]
    stats = {entry[0] for entry in wanted}
    alike = [row for row in rows if {entry[0] for entry in effect(mod_rows, base_mods(row))} == stats]
    return max(same or alike or rows, key=lambda row: row.get("DropLevel") or 0)


def own_implicits(mod_rows, implicits, base_implicits, eldritch):
    """The implicits that replace the base's, or None to keep them."""
    if not base_implicits:
        return implicits or None
    if not implicits or effect(mod_rows, implicits) == effect(mod_rows, base_implicits):
        return None
    return implicits if eldritch or any("Unique" in mod_id for mod_id, _ in implicits) else None


def overrides(name, mod_rows, mods, implicit=False):
    """The ranges that differ from Mods.dat, as a RollRange each. The export can tie a line to a row of the same stats
    with another roll; PoB's roll is the unique's then, unless STALE_ROLLS says otherwise."""
    result = []
    for mod_id, ranges in mods:
        if (name, mod_id) in STALE_ROLLS:
            continue
        row = mod_rows[mod_id]
        for index, (low, high) in enumerate(ranges):
            if sorted((row.get(f"Stat{index + 1}Min"), row.get(f"Stat{index + 1}Max"))) != sorted((low, high)):
                section = ", Implicit: true" if implicit else ""
                result.append(f'new("{mod_id}", {index}, {min(low, high)}, {max(low, high)}{section})')
    return result


def text_map(path):
    """ModTextMap.lua: a mod's text with # for each number, lower case, to its Mods.dat ids."""
    source = path.read_text(encoding="utf-8", errors="replace")
    entries = re.findall(r'^\s*\["((?:[^"\\]|\\.)*)"\] = \{(.*?)\},?\s*$', source, re.M)
    return {key.lower(): re.findall(r'"([^"]+)"', ids) for key, ids in entries}


def name_key(name):
    """As UniqueCatalog.NameKey: Words writes some apostrophes otherwise, and spells three names its own way."""
    key = name.replace("\u2019", "'").replace("\u2018", "'").replace("\u02bc", "'")
    return NAME_SPELLINGS.get(key, key).lower()


def csharp(values):
    return "[" + ", ".join(json.dumps(value, ensure_ascii=False) for value in values) + "]"


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--pob", type=pathlib.Path, default=POB, help="the Path of Building folder")
    args = parser.parse_args()
    export = args.pob / "src/Export/Uniques"
    texts = text_map(export / "ModTextMap.lua")

    base = json.load(open(ROOT / "data/tables/BaseItemTypes.json", encoding="utf-8"))
    mods = json.load(open(ROOT / "data/tables/Mods.json", encoding="utf-8"))
    mod_rows = {row["Id"]: row for row in mods}
    words = json.load(open(ROOT / "data/tables/Words.json", encoding="utf-8"))
    client_names = {name_key(row["Text"]) for row in words if row["Wordlist"] == 6 and row["Text"]}
    by_name = defaultdict(list)
    for row in base:
        by_name[row["Name"]].append(row)

    def candidates(name):
        # Some newer bases carry a parenthesised subtype in PoB but not in the client table.
        return by_name.get(name) or by_name.get(name.split(" (", 1)[0]) or []

    rows = []
    skipped = []
    unknown = []
    for path in sorted(export.glob("*.lua")):
        if path.name == "ModTextMap.lua":
            continue
        for name, lines in blocks(path):
            if name_key(name) not in client_names:
                skipped.append(f"{path.name}: {name} (no unique name in Words)")
                continue
            bases, meta, implicit_lines, mod_lines = split_block(lines, candidates)
            chosen = chosen_variants(meta)

            base_name = next((text for text in (in_variant(line, chosen[:1]) for line in bases) if text), None)
            base_name = base_name or tagged(bases[-1])[0]
            found = candidates(base_name)
            if not found:
                skipped.append(f"{path.name}: {name} ({base_name})")
                continue

            flags = set()
            lost = []
            implicits = resolve(implicit_lines or [], chosen, mod_rows, texts, lost, flags,
                                (UNIQUE_MOD, EXARCH_IMPLICIT, EATER_IMPLICIT))
            fractured = set()
            explicits = resolve(mod_lines, chosen, mod_rows, texts, lost, flags, fractured=fractured)
            # The client counts the fractured explicits from the first (13-items.md, Fracturing Orb).
            explicits = [mod for i, mod in enumerate(explicits) if i in fractured] + \
                        [mod for i, mod in enumerate(explicits) if i not in fractured]
            unknown += [f"{name}: {text}" for text in lost]

            def base_mods(row):
                return [(mods[key]["Id"], []) for key in row.get("Implicit_ModsKeys") or []]

            chosen_base = choose_base(found, implicits, mod_rows, base_mods)
            eldritch = any(line in ELDRITCH for line in meta)
            own = own_implicits(mod_rows, implicits, base_mods(chosen_base), eldritch)
            rows.append({
                "name": name,
                "base": chosen_base["Id"],
                "mods": [mod_id for mod_id, _ in explicits],
                # Only what differs from the base type, whose implicits ItemCatalog.Create rolls by itself.
                "implicits": None if own is None else [mod_id for mod_id, _ in own],
                "ranges": overrides(name, mod_rows, own or [], implicit=True) + overrides(name, mod_rows, explicits),
                "corrupted": "Corrupted" in meta or "Corrupted" in flags or name in CORRUPTED,
                "mirrored": "Mirrored" in flags,
                "fractured": len(fractured),
                "influences": sorted(INFLUENCES[text] for text in {in_variant(line, chosen) for line in meta} | flags
                                     if text in INFLUENCES),
            })

    # Stable output; duplicate names (Stormblood, Grand Spectrum, Combat Focus) keep every base variant.
    rows.sort(key=lambda row: (row["name"].lower(), row["base"].lower()))
    names = Counter(row["name"] for row in rows)
    repeated = [key for key, count in Counter((row["name"], row["base"]) for row in rows).items() if count > 1]
    if repeated:
        raise SystemExit(f"two rows for one catalogue key: {repeated}")
    lines = [
        "// Generated from Path of Building src/Export/Uniques/*.lua for the target 3.29.3b data.",
        "// Do not edit by hand; regenerate with scripts/re/unique_items.py (a read-only third-party reference).",
        "using GameServer.Core.Protocol.Instance;",
        "",
        "/// <summary>The full unique-item seed: name, base type, Mods.dat ids and the unique's implicits. The catalogue",
        "/// table keeps the runtime rows; this array is the fresh-database fallback.</summary>",
        "static class UniqueItemSeed",
        "{",
        "    public static readonly UniqueCatalog.Definition[] All =",
        "    [",
    ]
    for row in rows:
        options = [csharp(row["mods"])]
        if row["implicits"] is not None:
            options.append(f"Implicits: {csharp(row['implicits'])}")
        if row["ranges"]:
            options.append(f"Ranges: [{', '.join(row['ranges'])}]")
        if row["corrupted"]:
            options.append("Corrupted: true")
        if row["mirrored"]:
            options.append("Mirrored: true")
        if row["fractured"]:
            options.append(f"Fractured: {row['fractured']}")
        if row["influences"]:
            options.append("Influences: " + " | ".join(f"ItemInfluence.{name}" for name in row["influences"]))
        lines.append(f'        new({json.dumps(row["name"], ensure_ascii=False)}, {json.dumps(row["base"])}, '
                     f'{", ".join(options)}),')
    lines += ["    ];", "}", ""]
    OUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"wrote {len(rows)} unique definitions to {OUT}")
    print("skipped:", len(skipped))
    for entry in skipped:
        print("  ", entry)
    print("lines left out, no Mods.dat row found:", len(unknown))
    for entry in sorted(unknown):
        print("  ", entry)
    print("corrupted:", sum(1 for row in rows if row["corrupted"]))
    print("with their own implicits:", sum(1 for row in rows if row["implicits"] is not None))
    print("with roll overrides:", sum(1 for row in rows if row["ranges"]))
    print("influenced:", sum(1 for row in rows if row["influences"]))
    print("duplicate names:", [(name, count) for name, count in names.items() if count > 1])


if __name__ == "__main__":
    main()
