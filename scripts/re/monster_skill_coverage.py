# usage: python -I scripts/re/monster_skill_coverage.py [--csv <file>] [--cache <folder>] [--no-cache]
#
# Coverage of the monster skill data, per MonsterVarieties row, as numbers a change can be measured by. Prints a summary
# table (totals and % per category) and writes one CSV row per variety (default G:/temp/monster-skill-coverage.csv if
# G:/temp exists, else the system temp folder). Nothing it writes is committed.
#
# Categories (exclusive, in this order): campaign boss (named in WorldAreas Bosses_MonsterVarietiesKeys of an area with a
# town), map bar boss (BossHealthBar), league (Id has "League"), ordinary. `controller` is a separate flag: the variety's
# Id (or a folder prefix of it) appears as a string literal in a hand-scripted encounter under Modules/Core/Game/Monster,
# so a change must leave it as it is.
#
# Columns per variety:
#   kit        usable effects (mirror of BossKits.IsUsable), kept by our kit (a kit boss: all; anything else: the effects
#              MonsterKits.IsExcludedOrdinaryEffect leaves, at most 5), dropped = usable - kept.
#   clips      for each projectile / mortar effect (SkillCatalog.BuildMonsterProjectiles(true) keys), whether a clip with a
#              release is found: `ours` = the animation key the server uses (SkillCatalog.MonsterContact on the
#              monster_contact_clips pool), `ge` = GrantedEffects.Animation as the key, `act` = the variety's .act (cache)
#              has an entry for either key at the root or in a stance (any form: item_animation, effect_locked, ...).
#   projectile source of every projectile effect: monster table (mortar / spell row), variation (weapon row), class
#              (ActionClasses), art (SkillArtVariations), own (Metadata/Projectiles/<effect>), borrowed (another effect
#              on the same ActiveSkills row), none; the order BossKits.ArtShot tries them in.
#   combo      EmptyAction parents (GrantedEffects Id has "EmptyAction", Animation set) with children (Animation NULL).
#   states     shared states the .ot / .otc chain declares (define_shared_state), from the cache.
#
# The rules are a Python mirror of the C# above (SkillCatalog, BossKits, MonsterKits); they are not the server's own run,
# so the totals can drift from it (not mirrored: BossSummons overrides, stance clips, MonsterKits shaping, kit order).
# The file cache is scripts/re/monster_files_cache.py's.
import csv, glob, json, os, re, sys, tempfile
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(HERE)), "scripts", "dev"))
import clientdb  # noqa: E402
import ot_components as oc  # noqa: E402

REPO = oc.REPO
CACHE = os.path.join(REPO, "Tools", "re", "out", "monster-files")
MAX_ORDINARY = 16  # MonsterKits.MaxOrdinaryKitSkills
CATEGORIES = ["campaign", "map", "league", "ordinary"]

USABLE_TOKENS_ALWAYS = ["Teleport", "MemoryGame", "Bond", "Mark", "Souls", "Emerge", "Retreat", "EmptyAction"]
EXCLUDED_ORDINARY = ["Spectre", "WhileSpectred", "EmptyAction", "Wait", "Taunt", "ChangeToStance", "ClaimSoldier",
                     "ExecuteGeal", "SuicideExplosion", "Teleport", "Daemon", "MemoryGame", "Bond", "Mark", "Souls", "Emerge",
                     "Retreat", "Burrow", "Impact", "Trigger", "NoEffects", "InAoE", "OutAoE", "Flee",
                     "SpiderGodActivateLaser", "MapBoss", "Beacon"]
KIT_BOSS_IDS = {"Metadata/Monsters/Frog/FrogGod/SilverFrogImage",  # Yugul.Image
                "Metadata/Monsters/LunarisSolaris/SolarisCelestialForm", "Metadata/Monsters/LunarisSolaris/LunarisCelestialForm"}


class Data:
    def __init__(self):
        t = clientdb.table
        self.varieties = t("MonsterVarieties")
        self.ge = t("GrantedEffects")
        self.active = t("ActiveSkills")
        self.type_ids = [r.get("Id") for r in t("ActiveSkillType")]
        self.stat_ids = [r.get("Id") for r in t("Stats")]
        self.stat_row = {s: i for i, s in enumerate(self.stat_ids) if s}
        self.animations = [r.get("Id") or "" for r in t("Animation")]
        self.projectiles = t("Projectiles")
        self.projectile_row = {r.get("Id"): i for i, r in enumerate(self.projectiles)}
        self.action_types = [r.get("Id") for r in t("ActionTypes")]
        self.types_of_active = [{self.type_ids[i] for i in (r.get("ActiveSkillTypes") or []) if 0 <= i < len(self.type_ids)}
                                for r in self.active]
        self.corpse_active = {i for i, r in enumerate(self.active) if 6 in (r.get("ActiveSkillTargetTypes") or [])}
        self.stats = self._stats()
        self.active_id_rows = defaultdict(list)
        for e, r in enumerate(self.ge):
            if r.get("ActiveSkill") is not None:
                self.active_id_rows[r["ActiveSkill"]].append(e)

    def _stats(self):
        """{effect row: {stat id: value}}: the stat set's implicit (1) and constant stats, then the lowest level's extras."""
        sets = clientdb.table("GrantedEffectStatSets")
        lowest = {}
        for s, lvl, stats, values in clientdb.connect().execute(
                "SELECT StatSet, GemLevel, AdditionalStats, AdditionalStatsValues FROM GrantedEffectStatSetsPerLevel"):
            if s not in lowest or lvl < lowest[s][0]:
                lowest[s] = (lvl, json.loads(stats or "[]"), json.loads(values or "[]"))
        by_set = {}
        for s, row in enumerate(sets):
            d = {}
            for k in row.get("ImplicitStats") or []:
                d[self.stat_ids[k]] = 1
            for k, v in zip(row.get("ConstantStats") or [], row.get("ConstantStatsValues") or []):
                d[self.stat_ids[k]] = v
            if s in lowest:
                for k, v in zip(lowest[s][1], lowest[s][2]):
                    d[self.stat_ids[k]] = d.get(self.stat_ids[k], 0) + v
            by_set[s] = d
        return {e: by_set.get(r.get("StatSet1"), {}) for e, r in enumerate(self.ge)}

    def types(self, e):
        a = self.ge[e].get("ActiveSkill")
        return self.types_of_active[a] if a is not None and a < len(self.types_of_active) else set()

    def eid(self, e):
        return self.ge[e].get("Id") or ""

    def action_type(self, e):
        a = self.ge[e].get("ActiveSkill")
        t = self.active[a].get("ActionType") if a is not None and a < len(self.active) else None
        return self.action_types[t] if t is not None and t < len(self.action_types) else None

    def stat(self, e, name):
        return self.stats[e].get(name, 0)


def castable_before(d, e):
    return d.stat(e, "cast_on_death_%") > 0 or d.stat(e, "attack_on_death_%") > 0


def is_usable(d, e):
    """BossKits.IsUsable(forSummon: false)."""
    if e < 0 or e >= len(d.ge) or castable_before(d, e) or d.stat(e, "cast_on_damage_taken_%") > 0:
        return False
    t = d.types(e)
    if "Movement" in t or "Aura" in t:
        return False
    i = d.eid(e)
    if i == "EASMercenaryPortalOut":
        return False
    if any(tok in i for tok in USABLE_TOKENS_ALWAYS + ["Daemon"]) or i.startswith("SpiderGodActivateLaser"):
        return False
    if i.startswith("GA") and "Leap" in i and i.endswith("Damage") and d.action_type(e) == "GeometryAttack":
        return False
    if not ({"Attack", "Spell", "Projectile"} & t):
        return False
    return d.ge[e].get("ActiveSkill") not in d.corpse_active


def ordinary_excluded(d, e):
    """MonsterKits.IsExcludedOrdinaryEffect."""
    i = d.eid(e)
    if not i or castable_before(d, e) or d.stat(e, "cast_on_damage_taken_%") > 0 or "Aura" in d.types(e):
        return True
    if "Triggered" in d.types(e) or "RemoteMined" in d.types(e):
        return True
    if i.startswith(("GS", "GT", "EAS", "GA")) or any(tok in i for tok in EXCLUDED_ORDINARY):
        return True
    return d.stat(e, "skill_unusable") != 0


class Projectiles:
    """SkillCatalog.BuildMonsterProjectiles(true), VariationProjectile, SkillArtProjectile, SharedProjectile."""

    def __init__(self, d):
        t = clientdb.table
        self.d = d
        anim = lambda a: d.animations[a] if isinstance(a, int) and 0 <= a < len(d.animations) and d.animations[a] else None
        self.mortar = {r["Id"]: (r.get("Projectile"), anim(r.get("Animation"))) for r in t("MonsterMortar") if r.get("Id") is not None}
        self.attack = {r["Id"]: r.get("Projectile") for r in t("MonsterProjectileAttack") if r.get("Id") is not None}
        self.spell = {r["Id"]: (r.get("Projectile"), anim(r.get("Animation"))) for r in t("MonsterProjectileSpell") if r.get("Id") is not None}
        skill_ids = {r.get("Id"): i for i, r in enumerate(d.active)}
        self.mortar_skills = {i: r["Id"] == "monster_mortar_attack" for i, r in enumerate(d.active) if (r.get("Id") or "").startswith("monster_mortar")}
        by_id = lambda name: {i for i, r in enumerate(d.active) if r.get("Id") == name}
        self.spell_skills, self.magma, self.merveil, self.throw, self.weapon = (
            by_id("monster_projectile_spell"), by_id("rolling_magma"), by_id("merveil_ice_spear"), by_id("spectral_throw"),
            by_id("monster_projectile_weapon"))
        self.table = {}   # effect -> (animation key, source)
        for e, r in enumerate(d.ge):
            a = r.get("ActiveSkill")
            if a is None:
                continue
            if a in self.mortar_skills:
                skill = r.get("SkillID")
                m = self.mortar.get(skill)
                if m is not None and self._ok(m[0]):
                    self.table[e] = (m[1], "monster table")
            elif a in self.spell_skills:
                n = d.stat(e, "monster_projectile_variation")
                s = self.spell.get(n)
                if s is not None and self._ok(s[0]):
                    self.table[e] = (s[1] or "SpellProjectile", "monster table")
            elif a in self.magma and "Metadata/Projectiles/MagmaOrbShort" in d.projectile_row:
                self.table[e] = ("SpellProjectile", "monster table")
            elif a in self.merveil and "Metadata/Projectiles/IceSpear" in d.projectile_row:
                self.table[e] = ("SpellProjectile", "monster table")
            elif a in self.throw and r.get("Id") == "FairgravesThrownWeapon":
                self.table[e] = ("Attack", "monster table")
            elif a in self.weapon:
                n = d.stats[e].get("monster_projectile_variation")
                if n is not None and self._ok(self.attack.get(n)):
                    self.table[e] = ("SpellProjectile", "variation")
        art = t("SkillArtVariations")
        self.art_projectile = [r.get("ProjectileArt") or [] for r in art]
        self.art_by_effect, self.art_by_active = defaultdict(list), defaultdict(list)
        for i, r in enumerate(art):
            for e in r.get("GrantedEffect") or []:
                self.art_by_effect[e].append(i)
            for a in r.get("ActiveSkill") or []:
                self.art_by_active[a].append(i)
        self.art_ids, self.directory = {}, {}
        for i, r in enumerate(t("ProjectilesArtVariations")):
            self.art_ids[i] = r.get("Id") or ""
            self.directory.setdefault((r.get("Id") or "", r.get("Variant") or 0), r.get("Projectiles") or [])
        self.class_types = {"TetheringArrow", "TotemSnipe", "Flamethrower", "TotemFireSpray"}
        self.suffixes = ["Royale", "AltX", "AltY", "AltZ"]

    def _ok(self, row):
        return isinstance(row, int) and 0 <= row < len(self.d.projectiles) and bool(self.d.projectiles[row].get("Id")) \
            and (self.d.projectiles[row].get("ProjectileSpeed") or 0) > 0

    def _named(self, e):
        """SkillCatalog.ProjectileFor: the effect's own Projectiles row by name (and its base effect's)."""
        i = self.d.eid(e)
        names = [i]
        for s in self.suffixes:
            if i.endswith(s):
                names.append(i[:-len(s)])
                break
        return any("Metadata/Projectiles/" + n in self.d.projectile_row for n in names if n)

    def art(self, e):
        variant = self.d.ge[e].get("__col23") or 0
        a = self.d.ge[e].get("ActiveSkill")
        for row in self.art_by_effect.get(e, []) + (self.art_by_active.get(a, []) if a is not None else []):
            slots = self.art_projectile[row]
            if slots and self.art_ids.get(slots[0]) is not None and self.directory.get((self.art_ids[slots[0]], variant)):
                return True
        return False

    def source(self, e):
        """Where the projectile of an effect comes from, in BossKits.ArtShot's order; "none" when it has none."""
        if e in self.table:
            return self.table[e][1]
        if self.d.action_type(e) in self.class_types:
            return "class"
        if self.art(e):
            return "art"
        a = self.d.ge[e].get("ActiveSkill")
        if self._named(e):
            return "own"
        if a is not None and a not in self.weapon:
            if any(o != e and self._named(o) for o in self.d.active_id_rows[a]):
                return "borrowed"
        return "none"


def load_controllers():
    """Ids and folder prefixes the hand-scripted encounters name (string literals in Modules/Core/Game/Monster)."""
    exact, prefixes = set(), set()
    pattern = re.compile(r'"(Metadata/Monsters?/[^"]+)"')
    for path in glob.glob(os.path.join(REPO, "Modules", "Core", "Game", "Monster", "*.cs")):
        name = os.path.basename(path)
        if not (re.search(r"Encounter|Sirus|Kitava|Trinity|Oversoul|Doedre|BrineKing|Arakaali|KingIn|AtlasBoss|Act\dTo\d|Shaper|Elder"
                          r"|Yugul|Dominus|Izaro|Maven|Tukohama|Solaris|DarkMonarch|AbberathEnc|Avarius", name)):
            continue
        with open(path, encoding="utf-8") as f:
            for lit in pattern.findall(f.read()):
                (prefixes if lit.endswith("/") else exact).add(lit)
    return exact, prefixes


def act_keys(text):
    """Every key an .act sets at the root or in a stance (any form)."""
    keys = set()
    for m in re.finditer(r'^\s*([A-Za-z_][A-Za-z_0-9]*)\s*=\s*"', text, re.M):
        keys.add(m.group(1))
    return keys


def main():
    args = sys.argv[1:]
    cache = args[args.index("--cache") + 1] if "--cache" in args else CACHE
    use_cache = "--no-cache" not in args and os.path.isdir(cache) and any(True for _ in os.scandir(cache))
    default_csv = os.path.join("G:/temp" if os.path.isdir("G:/temp") else tempfile.gettempdir(), "monster-skill-coverage.csv")
    out_csv = args[args.index("--csv") + 1] if "--csv" in args else default_csv
    d = Data()
    pr = Projectiles(d)
    oc.CACHE = cache
    areas = clientdb.table("WorldAreas")
    names = [r.get("Id") or "" for r in d.varieties]
    campaign = {names[k] for a in areas if a.get("ParentTown_WorldAreasKey") is not None
                for k in (a.get("Bosses_MonsterVarietiesKeys") or []) if 0 <= k < len(names)}
    exact, prefixes = load_controllers()

    contacts = clientdb.table("monster_contacts")
    clips_rows = clientdb.table("monster_contact_clips")
    by_profile = bool(contacts and "profile" in contacts[0] and clips_rows and "profile" in clips_rows[0])
    contact_row = {r["variety"]: (r.get("profile") if by_profile else i) for i, r in enumerate(contacts)}
    pools = defaultdict(lambda: defaultdict(list))  # profile or variety row -> pool -> [contact]
    owner_col = "profile" if by_profile else "monster"
    for r in clips_rows:
        if r.get("animation") is not None and r.get("index") is not None and (r.get("length") or 0) > 0:
            pools[r[owner_col]][r["pool"]].append(r.get("contact"))
    state_cache, act_cache, parsed_cache = {}, {}, {}

    def shared_states(vid):
        if not use_cache:
            return None
        if vid in state_cache:
            return state_cache[vid]
        out, seen, todo = [], set(), [vid]
        while todo:
            p = todo.pop(0)
            if p in seen or p == "nothing":
                continue
            seen.add(p)
            for ext in (".ot", ".otc"):
                if (p, ext) not in parsed_cache:
                    text = oc.read_text(p + ext)
                    parsed_cache[(p, ext)] = None if text is None else oc.parse(text)
                parsed = parsed_cache[(p, ext)]
                if parsed is None:
                    continue
                out += [s[0] for s in parsed["shared_states"]]
                if ext == ".ot":
                    todo += parsed["extends"]
        state_cache[vid] = sorted(set(out))
        return state_cache[vid]

    def act_key_set(act):
        if act not in act_cache:
            text = oc.read_text(act)
            act_cache[act] = act_keys(text) if text is not None else None
        return act_cache[act]

    rows = []
    for vi, v in enumerate(d.varieties):
        vid = v.get("Id") or ""
        if not vid:
            continue
        effects = [e for e in (v.get("GrantedEffectsKeys") or []) if 0 <= e < len(d.ge)]
        cat = "campaign" if vid in campaign else "map" if v.get("BossHealthBar") else "league" if "League" in vid else "ordinary"
        controller = vid in exact or any(vid.startswith(p) for p in prefixes)
        usable = []
        for e in effects:
            if is_usable(d, e) and e not in usable:
                usable.append(e)
        kit_boss = (cat in ("campaign", "map") or vid in KIT_BOSS_IDS) and len(usable) > 1
        if kit_boss:
            kept = len(usable)
            filtered = len(usable)
        elif len(effects) <= 1:
            kept, filtered = 0, 0
        else:
            filtered = sum(1 for e in usable if not ordinary_excluded(d, e))
            kept = min(MAX_ORDINARY, filtered)
        # Clips and projectiles over the variety's granted effects.
        proj_effects = [e for e in dict.fromkeys(effects) if e in pr.table]
        all_proj = [e for e in dict.fromkeys(effects) if e in pr.table or ("Projectile" in d.types(e) and "Melee" not in d.types(e) and is_usable(d, e))]
        pool = pools.get(contact_row.get(vid), {})
        act_files = v.get("ACTFiles") or []
        key_sets = [act_key_set(a) for a in act_files] if use_cache else []
        ours = ge_key = act_found = 0
        for e in proj_effects:
            key = pr.table[e][0]
            alt = d.animations[d.ge[e]["Animation"]] if isinstance(d.ge[e].get("Animation"), int) and d.ge[e]["Animation"] < len(d.animations) else None
            has = lambda k: k is not None and any(c is not None for c in pool.get(k, []))
            o, g = has(key), has(alt)
            ours += o
            ge_key += o or g
            if use_cache:
                act_found += o or g or any(ks and ((key in ks) or (alt in ks)) for ks in key_sets)
        sources = Counter(pr.source(e) for e in all_proj)
        parents = [e for e in effects if "EmptyAction" in d.eid(e) and d.ge[e].get("Animation") is not None]
        children = [e for e in effects if e not in parents and d.ge[e].get("Animation") is None]
        combo = bool(parents and children)
        states = shared_states(vid)
        rows.append(dict(
            variety=vid, category=cat, controller=int(controller), granted=len(effects), usable=len(usable), kit_boss=int(kit_boss),
            kit_kept=kept, kit_dropped=len(usable) - kept, over5_raw=int(len(usable) > MAX_ORDINARY),
            over5_filtered=int(filtered > MAX_ORDINARY), proj_rows=len(proj_effects), clip_ours=ours, clip_ge=ge_key,
            clip_act=act_found if use_cache else "", **{f"src_{k}": sources.get(k, 0) for k in
                                                          ("monster table", "variation", "class", "art", "own", "borrowed", "none")},
            combo=int(combo), combo_parents=len(parents) if combo else 0, states=";".join(states) if states else "",
            has_states=int(bool(states)) if use_cache else ""))
    with open(out_csv, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0]))
        w.writeheader()
        w.writerows(rows)
    summarize(rows, use_cache, out_csv)


def summarize(rows, use_cache, out_csv):
    groups = {c: [r for r in rows if r["category"] == c] for c in CATEGORIES}
    groups["total"] = rows
    cols = CATEGORIES + ["total"]

    def line(label, fn, pct=True, of=len):
        cells = []
        for c in cols:
            rs = groups[c]
            n = fn(rs)
            cells.append(f"{n} ({100 * n / max(1, of(rs)):.1f}%)" if pct else str(n))
        print(f"{label:<44}" + "".join(f"{x:>18}" for x in cells))

    print(f"{'':<44}" + "".join(f"{c:>18}" for c in cols))
    line("varieties", lambda rs: len(rs), pct=False)
    line("hand-scripted controller", lambda rs: sum(r["controller"] for r in rs))
    line("kit boss (full kit)", lambda rs: sum(r["kit_boss"] for r in rs))
    line("usable effects > 5 (raw)", lambda rs: sum(r["over5_raw"] for r in rs))
    line("usable effects > 5 after ordinary filter", lambda rs: sum(r["over5_filtered"] for r in rs))
    line("varieties with a dropped usable effect", lambda rs: sum(r["kit_dropped"] > 0 for r in rs))
    line("usable effects dropped (sum)", lambda rs: sum(r["kit_dropped"] for r in rs), pct=False)
    line("combo (EmptyAction parent + child)", lambda rs: sum(r["combo"] for r in rs))
    line("combo parents (sum)", lambda rs: sum(r["combo_parents"] for r in rs), pct=False)
    line("projectile/mortar rows", lambda rs: sum(r["proj_rows"] for r in rs), pct=False)
    line("  rows with a clip (ours: server key)", lambda rs: sum(r["clip_ours"] for r in rs), of=lambda rs: sum(r["proj_rows"] for r in rs))
    line("  rows without a clip (ours)", lambda rs: sum(r["proj_rows"] - r["clip_ours"] for r in rs), pct=False)
    line("  rows with a clip (+ GE.Animation)", lambda rs: sum(r["clip_ge"] for r in rs), of=lambda rs: sum(r["proj_rows"] for r in rs))
    if use_cache:
        line("  rows with a clip (+ .act entry, any form)", lambda rs: sum(r["clip_act"] for r in rs), of=lambda rs: sum(r["proj_rows"] for r in rs))
    line("varieties with a projectile row w/o clip", lambda rs: sum(r["proj_rows"] > r["clip_ours"] for r in rs))
    for k in ("monster table", "variation", "class", "art", "own", "borrowed", "none"):
        line(f"projectile source: {k} (effects)", lambda rs, k=k: sum(r[f"src_{k}"] for r in rs), pct=False)
    if use_cache:
        line("varieties declaring shared states (.ot/.otc)", lambda rs: sum(r["has_states"] for r in rs))
        names = Counter(s for r in rows for s in r["states"].split(";") if s)
        print("most common shared states: " + ", ".join(f"{k} {n}" for k, n in names.most_common(12)))
    else:
        print("(no file cache: clip_act and states left out; run scripts/re/monster_files_cache.py)")
    print(f"per-variety CSV: {out_csv}")


if __name__ == "__main__":
    main()
