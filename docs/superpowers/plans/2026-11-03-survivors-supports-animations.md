# Survivors: all support interactions and skill animations

Follow-up to the 2026-11-02 checkpoint (PR #502). Two goals from the maintainer:

1. Verify every support gem's interaction with every run skill, using Path of Building's rules
   (`E:\PoE Reference Library Agent`, pinned PoB 16de4b82) and the game data.
2. Give every run skill a cast animation, and every projectile its flight and impact animation.

## Facts established before coding

- The eight supports' gem stats are: Added Fire `physical_damage_%_to_add_as_fire` 25/39% (L1/L20),
  Added Cold 4-6/152-228, Added Lightning 1-8/19-358, Faster Casting `base_cast_speed_+%` 20/39%,
  Increased Area `base_skill_area_of_effect_+%` 30/49%, Increased Critical
  `additional_base_critical_strike_chance` 200 + `critical_strike_chance_+%` 60/117%,
  Faster Projectiles `base_projectile_speed_+%` 50/69% + `projectile_damage_+%` 20/29%,
  Life Leech `life_leech_from_any_damage_permyriad` 200/390.
- Each of those stat names is consumed by the existing cast pipeline
  (`DamageStats.Conversion`, `SurvivorsGlobalAdded`, `SurvivorsFormulas.CastInterval`, `SkillArea`,
  `AddedBaseCriticalStrike`, `ProjectileSpeed`, `PlayerHitDamage`, `LeechSources`). The gap is not the
  formulas; it is **which support may link to which skill** and the absent matrix proof.
- `GemSupports.Resolve` already implements the game's `AllowedActiveSkillTypes` / `ExcludedActiveSkillTypes`
  compatibility, but the survivors mode ignores it. Matrix over the 30 run gems: every active damage skill
  has 6-8 compatible supports; Herald of Ash has 1 because its ActiveSkills row has no Damage type.
- `0x1d1` (Animation.dat row) **locks player input** for its duration (11-skills.md "Player hit recovery"),
  so it cannot be the cast animation. `0x1b2` kind 0 plays a rig clip without touching the action state
  ("0x1b2 merely replaced the clip while movement or server cast release continued").
- `data/animation-contacts.json` already holds each class's clip lengths and contacts, but not the rig
  **index** that `0x1b2` needs. `scripts/re/anim_contacts.py` has the `.amd` animation order, so it can
  write the indices (manifest `Version` 3 -> 4).
- Server-drawn survivor projectiles set no `ProjectilePresentation`, so the client never receives the
  projectile's **impact clip** (Fireball's `fireball_explode`, Ice Spear's `frozenspear_impact`, ...).
  `projectile-clips.json` has flight/impact for every projectile; `MonsterProjectilePresentation` already
  converts them.

## Task 1 - support compatibility

- Expose `GemSupports.Compatible` to the survivors adapter.
- `Game.SurvivorsCanSupport(conn, skillIndex, supportIndex)`: the game's type rules; a herald's run pulse
  is a damage spell area effect, so its synthetic type set adds Damage/Spell/Area.
- `SurvivorsRun.DealOffer` and `TryChoose` accept an optional `linkable` predicate; the offer only pairs
  compatible supports with learned skills; `/survivors link` refuses an incompatible pair by name.
- Tests: the predicate is honoured by the offer; every run gem has at least five compatible supports.

## Task 2 - the cast computation and the full matrix

- Split `SurvivorsCast` into `SurvivorsComputeCast` (pure: stats, interval, profile, hit, crit, leech,
  herald pulse) and the live part (positions, aims, scheduling, packets).
- The herald pulse reads the merged support stats (damage, spell damage, area) instead of only the run's
  blessings; Faster Casting already reaches its interval.
- Remove the player's `base_skill_area_of_effect_+%` from the merged dictionary: the panel's
  `skill_area_of_effect_+%` already includes it (`SkillArea.WithoutPlayerBase`), so the current cast
  counts it twice.
- Gateway test walks every run gem x every support: compatibility, the expected delta (fire gain, cold,
  lightning, interval, radius, crit, projectile speed/damage, leech), and the known no-op (Added Fire on a
  gem with no physical damage).
- Core test for the offer predicate.

## Task 3 - cast and impact animations

- `anim_contacts.py`: write `"indices"` per class; bump the extract-data manifest to version 4 and
  regenerate `data/animation-contacts.json`.
- `AnimationContacts`: `AnimationClip.Index`; case-insensitive clips; `CastClip(ao, weapon, kind)` for
  Attack / SpellProjectile / SpellAreaOfEffect / SpellBuff, weapon-specific then unarmed then any.
- `SurvivorsCast` sends `0x1b2` kind 0 (rig clip, speed = clip length / cast interval) once per cast that
  actually fires, for every skill and herald.
- The survivor shot gets `MonsterProjectilePresentation(Clips(metadata), flightSeconds)`, plus Fireball's
  area explosion and Kinetic Blast's cluster visual, so flight and impact clips play.
- Tests: every class/weapon/kind resolves a real index; the presentation has an impact clip for a known
  projectile (Fireball, Ice Spear, Ethereal Knives).

## Task 4 - verify

- Build 0/0; Core, Data, Gateway tests green.
- Live: trace shows the cast animation packet, the projectile flight/impact clips and each support's
  delta; the client does not crash and still walks.
- Update `Agents/docs/game/survivors-mode.md`, the checkpoint and PR #502.
