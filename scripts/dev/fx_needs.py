"""What every stat that nothing reads would need (fx_stats rows with handled = 'none'), worked out from its id and
tooltip: who it is about, or for the character's own stats its trigger, its condition and its kind of effect.

    python scripts/dev/fx_needs.py data/world.db              the needs, ranked by how many stats share them
    python scripts/dev/fx_needs.py data/world.db --sql FILE   the notes as an update for fx_stats (why, fit)

A first pass to plan by, not a checked answer."""
import collections, re, sqlite3, sys


def tags(stat,kind,text):
    T=[]; s=stat; t=text
    def has(p): return re.search(p,t) is not None
    # who or what it is about
    if re.search(r"^map_|^area_|^display_map|^sextant|scarab",s) or has(r"^(Area|Areas|Map|Monsters|Players|Found Items|Unique Boss|Rare Monsters|Magic Monsters|Strongboxes|Chests)\b"):
        if has(r"^(Monsters|Rare Monsters|Magic Monsters|Unique Boss)") or re.search(r"^map_(monsters?|boss)",s): T.append("area: monsters as actors")
        elif has(r"^Players") or re.search(r"^map_player",s): T.append("area: players in the area as target")
        elif has(r"(contains?|has patches|inhabited|additional .*(packs|Strongbox|Shrine|Breach|Abyss|Legion|Harbinger|Essence))"): T.append("area: content when the area is made")
        elif has(r"(Found|drop|Rarity|Quantity|Experience|Items)"): T.append("area: drops and rewards")
        else: T.append("area: other modifier")
    elif re.search(r"minion|totem|spectre|zombie|skeleton|golem|sentinel_|arbalist|animated|raging_spirit|phantasm|reaper|^summon",s): T.append("minions and totems as actors")
    elif re.search(r"^local_",s): T.append("item property")
    elif re.search(r"heist|expedition|blight|delve|incursion|betrayal|harvest|ultimatum|ritual|sanctum|necropolis|crucible|ancestral|primordial|tincture|graft|village|kalguur|archnemesis|bestiary|synthesis|legion|breach|abyss|delirium|essence|metamorph",s): T.append("league content")
    elif re.search(r"flask",s) or has(r"Flask"): T.append("flask system")
    elif kind=="no text": T.append("no tooltip: internal or skill data")
    if not T or T[0] in ("flask system",):
        trig=[]
        for name,p in (("on kill",r"\bon Kill|when you Kill|you Kill|Killing"),("on hit",r"\bon Hit\b|when you Hit|you Hit\b|Hitting"),("when hit",r"when Hit|Hits? you|when you are Hit|when they Hit you|taking .* from Hit|take a Critical Strike|when you take"),
            ("on block",r"Block"),("on critical strike",r"Critical Strike"),("on stun",r"\bStun"),("on skill use",r"when you use|you use a|when you Cast|when you Attack|Warcry"),("on death",r"on Death|when you die|Killing Blow"),
            ("on a timer",r"every \d+|Every \d+|every second|per second if"),("focus",r"Focus"),("on low or full life",r"reach(ing)? Low Life|on reaching")):
            if has(p): trig.append(name)
        cond=[]
        for name,p in (("recently",r"Recently"),("while",r"\bwhile\b"),("per something",r"\bper\b|for each|for every"),("against a kind of enemy",r"against|\bvs\b|Enemies that are"),("during an effect",r"during"),("if",r"\bif\b")):
            if has(p): cond.append(name)
        eff=[]
        for name,p in (("ground effect",r"Ground|Smoke Cloud|Tar\b"),("explosion or damage to nearby",r"Explode|nearby Enemies|Enemies within"),("recovery",r"\b(Gain|Recover|Lose|Regenerate|Leech)[^/]*(Life|Mana|Energy Shield)"),
            ("ailment",r"Ignite|Chill|Freeze|Shock|Poison|Bleed|Scorch|Brittle|Sap|Impale"),("buff or debuff",r"Onslaught|Unholy Might|Tailwind|Fortif|Elusive|Adrenaline|Phasing|Blind|Maim|Intimidat|Unnerve|Hinder|Taunt|Wither|Exposure|Curse|Mark\b"),
            ("charges",r"Charge"),("skill behaviour",r"Projectile|Area of Effect|Chain|Fork|Pierce|Cooldown|Duration|Cost|Trigger|Socketed|Supported")):
            if has(p): eff.append(name)
        if kind=="trigger" or trig: T.append("trigger: "+(", ".join(trig) if trig else "other"))
        if cond: T.append("condition: "+", ".join(cond))
        if eff: T.append("effect: "+", ".join(eff))
        if kind!="trigger" and not trig and not cond: T.append("a number or flag the calculator does not read")
    return T


def main():
    db = sqlite3.connect(sys.argv[1])
    if "--sql" in sys.argv:
        rows = db.execute("select stat,kind,mods,coalesce(text,'') from fx_stats where handled='none' and why is null order by stat").fetchall()
        q = lambda s: "'" + s.replace("'", "''") + "'"
        values = [f"({q(s)}, {q(k)}, 'none', 'No code reads this stat.', {q('Needs: ' + '; '.join(tags(s, k, t)))})" for s, k, m, t in rows]
        head = """-- Every stat nothing reads gets a first note of what it would need, worked out from its id and tooltip by
-- scripts/dev/fx_needs.py: who it is about (the area's monsters or players, minions and totems, an item's own
-- property, league content), or for the character's own stats its trigger, its condition and its kind of effect.
-- A first pass to plan by, not a checked answer (checked stays 0).
INSERT INTO fx_stats (stat, kind, handled, why, fit) VALUES
"""
        tail = "\nON CONFLICT(stat) DO UPDATE SET why = excluded.why, fit = excluded.fit WHERE fx_stats.handled = 'none' AND fx_stats.why IS NULL;\n"
        open(sys.argv[sys.argv.index("--sql") + 1], "w", newline="\n").write(head + ",\n".join(values) + tail)
        print(len(values), "stats")
        return
    rows = db.execute("select stat,kind,mods,coalesce(text,'') from fx_stats where handled='none'").fetchall()
    stats, mods = collections.Counter(), collections.Counter()
    for s, k, m, t in rows:
        first = tags(s, k, t)[0]
        stats[first] += 1
        mods[first] += m
    print(len(rows), "stats nothing reads")
    for need, n in stats.most_common():
        print(f"{n:>5} stats {mods[need]:>6} mods  {need}")


if __name__ == "__main__":
    main()
