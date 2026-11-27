#!/usr/bin/env python3
"""Read-only coverage reporter across all 10,691 MonsterVarieties (PR #1618 S0).

Usage:
    python -I scripts/re/monster_coverage.py [--json Tools/re/out/monster-coverage.json] [--compare baseline.json]

Measures skill kit sizes, projectile/mortar release clip coverage (auto-detecting monster_contacts
v8 per-variety or v9 profile-normalized schemas), EmptyAction* combo parent coverage, and .ot
state flag coverage. Strictly read-only on data/client.db and Tools/re/out/act-ot-cache/.
"""
import argparse
import json
import os
import re
import sys
import time
from collections import Counter

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.append(os.path.dirname(os.path.abspath(__file__)))
sys.path.append(os.path.join(REPO, "scripts", "dev"))
import clientdb  # noqa: E402
import monster_cache  # noqa: E402

EXCLUDED_ORDINARY_TOKENS = (
    "Spectre", "WhileSpectred", "EmptyAction", "Wait", "Taunt", "ChangeToStance",
    "ClaimSoldier", "ExecuteGeal", "SuicideExplosion", "Teleport", "Daemon",
    "MemoryGame", "Bond", "Mark", "Souls", "Emerge", "Retreat", "Burrow",
    "Impact", "Trigger", "NoEffects", "InAoE", "OutAoE", "Flee",
    "SpiderGodActivateLaser", "MapBoss", "Beacon",
)
BOSSKITS_EXCLUDED_TOKENS = (
    "Teleport", "Daemon", "MemoryGame", "Bond", "Mark", "Souls",
    "SpiderGodActivateLaser", "EmptyAction", "Emerge", "Retreat",
)
EXT_RE = re.compile(r'^\s*extends\s+(.+)$', re.M)
Q_RE = re.compile(r'"([^"]+)"')


def load_monster_contact_pools(db):
    """Load {varietyId: {poolName: (has_contact, has_any_event)}} from client.db (v8 or v9 schema)."""
    if not clientdb.has("monster_contacts") or not clientdb.has("monster_contact_clips"):
        return {}, {}, "none"
    mc_cols = {r[1] for r in db.execute('PRAGMA table_info("monster_contacts")')}
    clip_cols = {r[1] for r in db.execute('PRAGMA table_info("monster_contact_clips")')}
    has_events_table = clientdb.has("monster_contact_clip_events")

    clip_has_event = set()
    if has_events_table:
        for (cid,) in db.execute('SELECT DISTINCT clip FROM "monster_contact_clip_events"'):
            clip_has_event.add(cid)

    if "profile" in mc_cols and "profile" in clip_cols:
        schema = "v9-profile"
        prof_pools = {}
        for cid, prof, pool, contact in db.execute(
            'SELECT _row, profile, pool, contact FROM "monster_contact_clips"'
        ):
            p = prof_pools.setdefault(prof, {})
            prev_c, prev_e = p.get(pool, (False, False))
            has_c = contact is not None
            has_e = has_c or (cid in clip_has_event)
            p[pool] = (prev_c or has_c, prev_e or has_e)
        by_var = {}
        for vid, prof in db.execute('SELECT variety, profile FROM "monster_contacts"'):
            by_var[vid] = prof_pools.get(prof, {})
    else:
        schema = "v8-variety"
        id_by_row = {r[0]: r[1] for r in db.execute('SELECT _row, variety FROM "monster_contacts"')}
        by_var = {}
        for cid, mrow, pool, contact in db.execute(
            'SELECT _row, monster, pool, contact FROM "monster_contact_clips"'
        ):
            vid = id_by_row.get(mrow)
            if not vid:
                continue
            p = by_var.setdefault(vid, {})
            prev_c, prev_e = p.get(pool, (False, False))
            has_c = contact is not None
            has_e = has_c or (cid in clip_has_event)
            p[pool] = (prev_c or has_c, prev_e or has_e)

    stats = {
        "schema": schema,
        "varieties_in_table": db.execute('SELECT count(*) FROM "monster_contacts"').fetchone()[0],
        "clip_rows": db.execute('SELECT count(*) FROM "monster_contact_clips"').fetchone()[0],
        "event_rows": db.execute('SELECT count(*) FROM "monster_contact_clip_events"').fetchone()[0] if has_events_table else 0,
    }
    return by_var, stats, schema


def collect_coverage(cache_dir: str):
    db = clientdb.connect()
    mvs = db.execute(
        "SELECT _row, Id, Name, ACTFiles, AOFiles, GrantedEffectsKeys, BossHealthBar, "
        "BaseMonsterTypeIndex, AddonMonsterTypeIndex FROM MonsterVarieties ORDER BY _row"
    ).fetchall()
    ges = {r[0]: r for r in db.execute(
        "SELECT _row, Id, IsSupport, ActiveSkill, Animation, SkillID, StatSet1 FROM GrantedEffects"
    )}
    asks = {r[0]: r for r in db.execute("SELECT _row, Id, ActionType, ActiveSkillTypes FROM ActiveSkills")}
    ats = {r[0]: r[1] for r in db.execute("SELECT _row, Id FROM ActionTypes")}
    anims = {r[0]: r[1] for r in db.execute("SELECT _row, Id FROM Animation")}
    ast_ids = {r[0]: r[1] for r in db.execute("SELECT _row, Id FROM ActiveSkillType")}
    mortars = {r[0]: (r[1], r[2]) for r in db.execute("SELECT Id, Projectile, Animation FROM MonsterMortar")}
    pspell = {r[0]: (r[1], r[2]) for r in db.execute("SELECT Id, Projectile, Animation FROM MonsterProjectileSpell")}
    pattack = {r[0]: r[1] for r in db.execute("SELECT Id, Projectile FROM MonsterProjectileAttack")}
    projs = {r[0]: (r[1], r[2]) for r in db.execute("SELECT _row, Id, ProjectileSpeed FROM Projectiles")}

    wa_campaign_bosses = set()
    for pt, bkeys in db.execute("SELECT ParentTown_WorldAreasKey, Bosses_MonsterVarietiesKeys FROM WorldAreas"):
        if pt is not None:
            wa_campaign_bosses.update(json.loads(bkeys or "[]"))

    stat_rows = {
        name: db.execute("SELECT _row FROM Stats WHERE Id = ?", (name,)).fetchone()[0]
        for name in (
            "skill_unusable", "cast_on_death_%", "attack_on_death_%",
            "cast_on_damage_taken_%", "monster_projectile_variation",
        )
    }
    interesting = set(stat_rows.values())
    ss_const, ss_all = {}, {}
    for ss_row, ckeys, cvals in db.execute("SELECT _row, ConstantStats, ConstantStatsValues FROM GrantedEffectStatSets"):
        for k, v in zip(json.loads(ckeys or "[]"), json.loads(cvals or "[]")):
            if k in interesting:
                ss_const.setdefault(ss_row, {})[k] = v
                ss_all.setdefault(ss_row, {})[k] = v
    seen_ss = set()
    for ss_key, skeys, svals, akeys, avals in db.execute(
        "SELECT StatSet, FloatStats, BaseResolvedValues, AdditionalStats, AdditionalStatsValues "
        "FROM GrantedEffectStatSetsPerLevel ORDER BY _row"
    ):
        if ss_key in seen_ss:
            continue
        seen_ss.add(ss_key)
        for k, v in zip(json.loads(skeys or "[]"), json.loads(svals or "[]")):
            if k in interesting:
                ss_all.setdefault(ss_key, {}).setdefault(k, int(v))
        for k, v in zip(json.loads(akeys or "[]"), json.loads(avals or "[]")):
            if k in interesting:
                ss_all.setdefault(ss_key, {}).setdefault(k, int(v))

    def ge_stat(e, name):
        return ss_all.get(ges[e][6], {}).get(stat_rows[name], 0)

    pools_by_var, mc_stats, schema = load_monster_contact_pools(db)

    # 1. Kits
    gt5_raw_non_campbar = gt5_active_non_campbar = gt5_bk_non_campbar = gt5_mk_non_campbar = 0
    # 2. Projectiles & mortars
    proj_total = 0
    proj_miss_v8key = proj_miss_s2key = proj_miss_s2contact = proj_miss_s2event = 0
    proj_miss_s2_vars = set()
    ge_var_const, mv_var_const = set(), set()
    # 3. EmptyAction combos
    ea_id_vars, ea_id_parents = set(), 0
    ea_at_vars, ea_at_parents, ea_at_animated, ea_at_in_contacts, ea_at_has_event = set(), 0, 0, 0, 0

    for row_id, vid, name, af, aof, gks, bhb, base, addon in mvs:
        eks = json.loads(gks or "[]")
        is_camp_or_bar = bool(bhb) or (row_id in wa_campaign_bosses)
        if len(eks) > 5 and not is_camp_or_bar:
            gt5_raw_non_campbar += 1

        bk_seen, mk_seen, active_seen = set(), set(), set()
        vpools = pools_by_var.get(vid, {})

        for e in eks:
            if e not in ges:
                continue
            ge = ges[e]
            gid = ge[1] or ""
            if stat_rows["monster_projectile_variation"] in ss_const.get(ge[6], {}):
                ge_var_const.add(e)
                mv_var_const.add(row_id)
            if ge[3] not in asks:
                continue
            ask = asks[ge[3]]
            aid = ask[1] or ""
            atn = ats.get(ask[2], "")
            types = {ast_ids.get(t) for t in json.loads(ask[3] or "[]")}
            ga = anims.get(ge[4]) if ge[4] is not None else None

            # Kit buckets
            if not ge[2] and gid:
                is_cod = ge_stat(e, "cast_on_death_%") > 0 or ge_stat(e, "attack_on_death_%") > 0
                is_codt = ge_stat(e, "cast_on_damage_taken_%") > 0
                is_unusable = ge_stat(e, "skill_unusable") != 0
                if ("Aura" not in types and not is_cod and not is_codt and "EmptyAction" not in gid
                        and ({"Attack", "Spell", "Projectile"} & types)):
                    active_seen.add(e)
                if (not is_cod and not is_codt and "Aura" not in types and "Movement" not in types
                        and not any(tok in gid for tok in BOSSKITS_EXCLUDED_TOKENS)
                        and ({"Attack", "Spell", "Projectile"} & types)):
                    bk_seen.add(e)
                if (not is_cod and not is_codt and not is_unusable
                        and "Aura" not in types and "Triggered" not in types and "RemoteMined" not in types
                        and not gid.startswith(("GS", "GT", "EAS", "GA"))
                        and not any(tok in gid for tok in EXCLUDED_ORDINARY_TOKENS)
                        and ({"Attack", "Spell", "Projectile", "Movement", "Travel", "Warcry", "Hex", "Buff"} & types)):
                    mk_seen.add(e)

            # EmptyAction* combos
            if "EmptyAction" in gid:
                ea_id_vars.add(row_id)
                ea_id_parents += 1
            if atn.startswith("EmptyAction"):
                ea_at_vars.add(row_id)
                ea_at_parents += 1
                if ga:
                    ea_at_animated += 1
                    if ga in vpools:
                        ea_at_in_contacts += 1
                        if vpools[ga][1]:
                            ea_at_has_event += 1

            # Projectiles & mortars (canonical BuildMonsterProjectiles(includeWeaponAttacks=True))
            table_anim = v8_key = class_row = None
            in_mp = False
            if aid.startswith("monster_mortar"):
                if ge[5] in mortars:
                    pr, a = mortars[ge[5]]
                    if pr in projs and projs[pr][0] and (projs[pr][1] or 0) > 0:
                        in_mp = True
                        table_anim = anims.get(a) if a is not None else None
                        v8_key = table_anim or "Melee"
                        class_row = table_anim or ("Melee" if aid == "monster_mortar_attack" else "SpellProjectile")
            elif aid == "monster_projectile_spell":
                v = ge_stat(e, "monster_projectile_variation")
                if v in pspell:
                    pr, a = pspell[v]
                    if pr in projs and projs[pr][0] and (projs[pr][1] or 0) > 0:
                        in_mp = True
                        table_anim = anims.get(a) if a is not None else None
                        v8_key = table_anim or "SpellProjectile"
                        class_row = table_anim or "SpellProjectile"
            elif aid == "monster_projectile_weapon":
                if stat_rows["monster_projectile_variation"] in ss_all.get(ge[6], {}):
                    v = ss_all[ge[6]][stat_rows["monster_projectile_variation"]]
                    if v in pattack:
                        pr = pattack[v]
                        if pr in projs and projs[pr][0] and (projs[pr][1] or 0) > 0:
                            in_mp = True
                            v8_key = "SpellProjectile"
                            class_row = "Melee"
            elif aid in ("rolling_magma", "merveil_ice_spear"):
                in_mp = True
                v8_key = class_row = "SpellProjectile"
            elif aid == "spectral_throw" and gid == "FairgravesThrownWeapon":
                in_mp = True
                v8_key = "Attack"
                class_row = "Melee"

            if in_mp:
                proj_total += 1
                if v8_key not in vpools:
                    proj_miss_v8key += 1
                keys = [k for k in (ga, class_row, table_anim) if k]
                matched = [vpools[k] for k in keys if k in vpools]
                if not matched:
                    proj_miss_s2key += 1
                    proj_miss_s2_vars.add(vid)
                if not any(c for c, _ in matched):
                    proj_miss_s2contact += 1
                if not any(ev for _, ev in matched):
                    proj_miss_s2event += 1

        if not is_camp_or_bar:
            if len(active_seen) > 5:
                gt5_active_non_campbar += 1
            if len(bk_seen) > 5:
                gt5_bk_non_campbar += 1
            if len(mk_seen) > 5:
                gt5_mk_non_campbar += 1

    # 4. .ot state flags (from cache if present)
    ot_summary = {"cache_available": False}
    mon_ot_root = os.path.join(cache_dir, "metadata", "monsters")
    if os.path.isdir(mon_ot_root):
        ot_cache = {}

        def parse_ot(p):
            np = monster_cache.norm_path(p if p.lower().endswith(".ot") else p + ".ot")
            if np in ot_cache:
                return ot_cache[np]
            txt = monster_cache.read_text(np, cache_dir)
            if txt is None:
                ot_cache[np] = None
                return None
            clean = re.sub(r"//[^\n]*", "", txt)
            parents = [q for m in EXT_RE.finditer(clean) for q in Q_RE.findall(m.group(1)) if q != "nothing"]
            shared, local = [], []
            for kind, dst in (("define_shared_state", shared), ("define_state", local)):
                for m in re.finditer(rf'\b{kind}\s*=\s*"([^"]*)"', clean):
                    for item in m.group(1).split(";"):
                        nm = item.partition("=")[0].strip()
                        if nm:
                            dst.append(nm)
            ot_cache[np] = (parents, shared, local)
            return ot_cache[np]

        all_mon_ots = [
            os.path.relpath(os.path.join(r, fn), cache_dir).replace("\\", "/")
            for r, _, fns in os.walk(mon_ot_root) for fn in fns if fn.endswith(".ot")
        ]
        unique_sh, unique_lo = set(), set()
        for p in all_mon_ots:
            info = parse_ot(p)
            if info:
                unique_sh.update(info[1])
                unique_lo.update(info[2])

        def chain_flags(start):
            seen, sh, lo = set(), set(), set()
            todo = [start]
            while todo:
                cur = todo.pop()
                np = monster_cache.norm_path(cur if cur.lower().endswith(".ot") else cur + ".ot")
                if np in seen or cur == "nothing":
                    continue
                seen.add(np)
                info = parse_ot(np)
                if not info:
                    continue
                sh.update(info[1])
                lo.update(info[2])
                todo.extend(info[0])
            return sh, lo

        mv_sh = mv_any = 0
        for _, vid, _, _, _, _, _, base, addon in mvs:
            root_id = vid if monster_cache.exists(vid + ".ot", cache_dir) else base
            sh, lo = chain_flags(root_id) if root_id else (set(), set())
            if addon and monster_cache.exists(addon + ".ot", cache_dir):
                ash, alo = chain_flags(addon)
                sh |= ash
                lo |= alo
            if sh:
                mv_sh += 1
            if sh or lo:
                mv_any += 1
        ot_summary = {
            "cache_available": True,
            "monster_ot_files": len(all_mon_ots),
            "unique_shared_states": len(unique_sh),
            "unique_local_states": len(unique_lo),
            "unique_states_union": len(unique_sh | unique_lo),
            "mv_varieties_with_shared_states": mv_sh,
            "mv_varieties_with_any_states": mv_any,
        }

    return {
        "generated_at_unix": int(time.time()),
        "total_varieties": len(mvs),
        "monster_contacts_table": mc_stats,
        "kits_gt5_non_campaign_or_bar": {
            "raw": gt5_raw_non_campbar,
            "active_combat": gt5_active_non_campbar,
            "bosskits_effects": gt5_bk_non_campbar,
            "monsterkits_ordinary": gt5_mk_non_campbar,
        },
        "variation_projectiles_const": {"granted_effects": len(ge_var_const), "varieties": len(mv_var_const)},
        "projectile_release_clips": {
            "total_rows": proj_total,
            "missing_v8_key": proj_miss_v8key,
            "missing_v8_key_pct": round(100.0 * proj_miss_v8key / max(1, proj_total), 2),
            "missing_s2_key": proj_miss_s2key,
            "missing_s2_key_pct": round(100.0 * proj_miss_s2key / max(1, proj_total), 2),
            "missing_s2_varieties": len(proj_miss_s2_vars),
            "missing_s2_contact": proj_miss_s2contact,
            "missing_s2_contact_pct": round(100.0 * proj_miss_s2contact / max(1, proj_total), 2),
            "missing_s2_any_event": proj_miss_s2event,
            "missing_s2_any_event_pct": round(100.0 * proj_miss_s2event / max(1, proj_total), 2),
        },
        "empty_action_combos": {
            "by_ge_id": {"varieties": len(ea_id_vars), "parents": ea_id_parents},
            "by_action_type": {
                "varieties": len(ea_at_vars),
                "parents": ea_at_parents,
                "animated_parents": ea_at_animated,
                "parents_with_contact_pool": ea_at_in_contacts,
                "parents_with_contact_pool_pct": round(100.0 * ea_at_in_contacts / max(1, ea_at_animated), 2),
                "parents_with_named_event": ea_at_has_event,
            },
        },
        "ot_state_flags": ot_summary,
    }


def print_summary(cur, base=None):
    pr = cur["projectile_release_clips"]
    ea = cur["empty_action_combos"]["by_action_type"]
    mc = cur["monster_contacts_table"]
    print(f"MonsterVarieties: {cur['total_varieties']} | monster_contacts schema={mc['schema']} "
          f"({mc['varieties_in_table']} vars, {mc['clip_rows']} clips, {mc['event_rows']} events)")
    print(f"  Projectiles missing clip (v8 key):  {pr['missing_v8_key']}/{pr['total_rows']} ({pr['missing_v8_key_pct']}%)")
    print(f"  Projectiles missing clip (S2 key):  {pr['missing_s2_key']}/{pr['total_rows']} ({pr['missing_s2_key_pct']}%, {pr['missing_s2_varieties']} vars)")
    print(f"  Projectiles missing any event:      {pr['missing_s2_any_event']}/{pr['total_rows']} ({pr['missing_s2_any_event_pct']}%)")
    print(f"  EmptyAction* parent clips covered:  {ea['parents_with_contact_pool']}/{ea['animated_parents']} ({ea['parents_with_contact_pool_pct']}%), with events={ea['parents_with_named_event']}")
    if base:
        bpr = base["projectile_release_clips"]
        bea = base["empty_action_combos"]["by_action_type"]
        print("--- Delta vs baseline ---")
        print(f"  Projectiles missing (v8->S2): {bpr['missing_v8_key']} ({bpr['missing_v8_key_pct']}%) -> "
              f"{pr['missing_s2_key']} ({pr['missing_s2_key_pct']}%) "
              f"[{pr['missing_s2_key'] - bpr['missing_v8_key']:+d} rows]")
        print(f"  EmptyAction* parent clips:    {bea['parents_with_contact_pool']} ({bea['parents_with_contact_pool_pct']}%) -> "
              f"{ea['parents_with_contact_pool']} ({ea['parents_with_contact_pool_pct']}%) "
              f"[{ea['parents_with_contact_pool'] - bea['parents_with_contact_pool']:+d}]")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--cache", default=monster_cache.DEFAULT_CACHE, help="Cache dir (default: Tools/re/out/act-ot-cache)")
    ap.add_argument("--json", default=os.path.join(REPO, "Tools", "re", "out", "monster-coverage.json"), help="Output JSON path")
    ap.add_argument("--compare", default=None, help="Optional baseline JSON to compare against")
    args = ap.parse_args()

    cur = collect_coverage(args.cache)
    os.makedirs(os.path.dirname(os.path.abspath(args.json)), exist_ok=True)
    with open(args.json, "w", encoding="utf-8") as f:
        json.dump(cur, f, indent=2)
    base = None
    if args.compare:
        with open(args.compare, encoding="utf-8") as f:
            base = json.load(f)
    print_summary(cur, base)


if __name__ == "__main__":
    main()
