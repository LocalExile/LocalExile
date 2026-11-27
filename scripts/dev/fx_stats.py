#!/usr/bin/env python3
"""The first sort of every stat for fx_stats (Agents/docs/dev/effect-rules.md, "The fx tables").

Reads the client tables (Mods, PassiveSkills, Stats, stat_descriptions) and the server's code, and writes one
INSERT per stat a mod or a passive carries: what kind of thing it is (from its name and tooltip line), whether any code
reads it, and how many mods carry it. A row that exists is left as it is (ON CONFLICT DO NOTHING): the sort is only
the starting point, and later updates correct it.

    python scripts/dev/fx_stats.py [--client data/client.db] [--out sql/updates/pending_world/15_fx_stats.sql]
"""
import argparse, collections, json, os, re, sqlite3, subprocess

p = argparse.ArgumentParser()
p.add_argument("--client", default="data/client.db")
p.add_argument("--out", default="sql/updates/pending_world/15_fx_stats.sql")
p.add_argument("--summary", action="store_true")
a = p.parse_args()
db = sqlite3.connect(f"file:{a.client}?mode=ro", uri=True)
stats = {row: name for row, name in db.execute('SELECT _row, Id FROM "Stats"')}

mods = collections.Counter()
for row in db.execute('SELECT StatsKey1,StatsKey2,StatsKey3,StatsKey4,StatsKey5,StatsKey6,StatsKey7,StatsKey8 FROM "Mods"'):
    mods.update({stats[k] for k in row if k is not None and k in stats})
carried = set(mods)
for (keys,) in db.execute('SELECT Stats FROM "PassiveSkills"'):
    carried.update(stats[k] for k in json.loads(keys or "[]") if k in stats)

# The tooltip line of each stat: the first text of the description block that lists it.
text = {}
for (block,) in db.execute("SELECT block FROM stat_descriptions"):
    lines = [l.strip() for l in block.replace("\r", "").split("\n")]
    if len(lines) < 4: continue
    first = re.search(r'"([^"]*)"', "\n".join(lines[3:]))
    if first:
        for name in lines[1].split()[1:]: text.setdefault(name, first.group(1).replace("\\n", " / "))

# The stats the server's code names: Stat.<Constant> and Stat.Of("name").
generated = open("src/GameServer.Gateway/Data/Stat.g.cs", encoding="utf8").read()
constant = {m.group(1): m.group(2) for m in re.finditer(r"public const uint (\w+) = \d+; // (\S+)", generated)}
reads = collections.defaultdict(set)
grep = subprocess.run(["grep", "-rnoE", "--include=*.cs", r'Stat\.(Of\("[^"]+"\)|[A-Z][A-Za-z0-9]+)', "Modules", "src"],
                      capture_output=True, text=True).stdout
for line in grep.splitlines():
    path, _, hit = line.split(":", 2)
    if path.endswith("Stat.g.cs"): continue
    name = hit[9:-2] if hit.startswith('Stat.Of("') else constant.get(hit[5:])
    if name: reads[name].add(path)

TRIGGER = re.compile(r" on (kill|hit|critical strike|block|death|use|low life)\b| when (you|hit|an? |your )|each time|every \d|when [a-z]+ed\b|on reaching|after ")
TRIGGER_NAME = re.compile(r"_on_(kill|hit|crit|block|death|use|stun)|_when_(hit|you|your)|^cast_|_triggers?_")
CONDITION = re.compile(r" while | if you| if | during | per | recently| against | for each |nearby| with (a|an|at least|\{)| to (attacks|spells) with ")
SKILL = re.compile(r"^(grants?|triggers?) level|^grants? (level|summon)|level \{\d\} [A-Z]")
NUMBER = re.compile(r"\{\d(:[^}]*)?\}")

def kind(name, line):
    if name.startswith("local_"): return "item property"
    if line is None: return "no text"
    low = " " + line.lower() + " "
    if SKILL.search(line.lower()) or re.search(r"(grants?|trigger)_level_|_skill_level$", name): return "grants a skill"
    if TRIGGER.search(low) or TRIGGER_NAME.search(name): return "trigger"
    if not NUMBER.search(line): return "flag"
    if CONDITION.search(low): return "conditional number"
    return "number"

q = lambda s: "NULL" if s is None else "'" + s.replace("'", "''") + "'"
rows, tally = [], collections.Counter()
for name in sorted(carried):
    line = text.get(name)
    k = kind(name, line)
    files = sorted(reads.get(name, ()))
    handled = "code" if files else "none"
    tally[(handled, k)] += 1
    code = None if not files else ", ".join(os.path.basename(f) for f in files[:3]) + (f" (+{len(files) - 3})" if len(files) > 3 else "")
    rows.append(f"({q(name)}, {q(k)}, {q(handled)}, {q(code)}, {mods.get(name, 0)}, {q(line)})")

if a.summary:
    for (handled, k), n in sorted(tally.items()): print(f"{handled:5} {k:20} {n}")
    print("total", len(rows))
else:
    head = ("-- The first sort of every stat a mod or a passive carries (scripts/dev/fx_stats.py): its kind from its name and\n"
            "-- tooltip line, whether the server's code names it (handled 'code', with the files) or nothing does ('none'),\n"
            "-- and how many Mods rows carry it. A guess until a later update corrects the row and sets checked = 1.\n")
    with open(a.out, "w", encoding="utf8", newline="\n") as f:
        f.write(head)
        for i in range(0, len(rows), 500):
            f.write("INSERT INTO fx_stats (stat, kind, handled, code, mods, text) VALUES\n" + ",\n".join(rows[i:i + 500])
                    + "\nON CONFLICT(stat) DO NOTHING;\n")
    print(len(rows), "stats ->", a.out)
