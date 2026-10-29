# Ancestral Commander

Ancestral Commander is the Marauder's Phrecia Juggernaut identity (`Juggernaut`, 3.29.3b), start node 17765.
No node grants passive points.
The defensive, warcry, strike and enemy-aura nodes have server consumers with Gateway coverage (Deathrattle only
partly); the Spirit and totem nodes are still open, as the checklist says.
Native acceptance is open: it needs the reviewed combined graph installed (see Admission below).

## Node checklist

| Node | Effect (wiki text, 3.29.3b values) | Consumer |
|---|---|---|
| 17765 Juggernaut | Start node, free | Implicit start of every allocation. |
| 11217 Armour and Life | 8% increased Armour, 4% increased maximum Life | Pools (`PassiveBonus`). |
| 9828 Armour and Life | as 11217 | Pools. |
| 19258 Armour and Life | as 11217 | Pools. |
| 26496 Armour and Life | as 11217 | Pools. |
| 65410 Armour and Life | as 11217 | Pools. |
| 41978 Armour and Life | as 11217 | Pools. |
| 8455 Armour and Life | as 11217 | Pools. |
| 24701 Armour and Life | as 11217 | Pools. |
| 28454 Armour and Life | as 11217 | Pools. |
| 29654 Armour and Life | as 11217 | Pools. |
| 23061 Ancestral Endurance | +2 to maximum Endurance Charges | `ChargePanelStats` maximum, which `Charges` gains up to. |
| 37852 Toughened Skin | Action and Movement Speed cannot be modified to below base; Armour from equipped Body Armour is doubled | Ailment action floor, pool movement floor, body armour defences. |
| 848 Ancestral Defiance | Gain 10% of missing unreserved Life before being Hit by an Enemy | Monster swings and dev enemy hits. |
| 47491 Ancestral Spirit | Choice of one Spirit (no stats of its own) | Paid choice parent; its options are free choice effects with a paid refund. |
| 59500 Spirit of Kahuturoa | Trigger level 20 Summon Spirit of Kahuturoa when you take a Critical Strike from a Unique | Not yet: no passive-granted triggered skills, and no allied minions. |
| 17511 Spirit of Utula | Trigger on taking a Savage Hit from a Unique | Not yet: as 59500. |
| 16639 Spirit of Kiloava | Trigger when you Block Damage from a Unique | Not yet: as 59500. |
| 62534 Spirit of Rakiata | Trigger on Critical Strike against a Marked Unique | Not yet: as 59500, and the server has no mark curses. |
| 17140 Spirit of Kaom | Trigger when a Totem dies while a Unique is in your Presence | Not yet: the server has no totems. |
| 48740 Call of the Ancestors | 200% increased Warcry Speed, 30% reduced Warcry Cooldown Recovery Rate | Warcry release time and one-use cooldown. |
| 3245 Deathrattle | Nearby corpses explode for 12% of their Life as Physical when you Warcry; Warcries open Chests | Partial: the warcry release explodes the server's corpses and opens quest chests; see below. |
| 40762 Ancestral Reach | Non-Vaal Strikes target 2 additional nearby Enemies, from 50% further away | Spirit strikes of gem Strikes and the default attack, picked as the client picks them; see below. |
| 61282 Ancestral Echo | Strikes also target the previous location they were used | Partial: the server strikes the previous location and sends `skill_location_memory`; the client's own strike there needs a live check. |
| 32105 Born under Fire | Nearby Enemies convert 50% of their Physical Damage to Fire | Monster hits within 60 cells convert, and the aura's buff goes on those monsters. |
| 16317 Forest of Ancestors | 20% of Damage from Hits is taken from your nearest Totem's Life before you; +1 maximum Totem | Not yet: the server has no totems. |

Three legal eight-point builds, with the four other Spirit options, cover every node:

```text
9828 61282 65410 40762 11217 37852 26496 32105
41978 48740 29654 3245 24701 23061 8455 848
19258 47491 59500 28454 16317 26496 32105 11217 37852   (59500 is the free choice effect)
```

The defensive nodes fit in one build: `11217 37852 24701 23061 8455 848 26496 9828`.

## How the effects are applied

- Armour and Life: `physical_damage_reduction_rating_+%` and `maximum_life_+%` add to the pools' increased buckets
  with the other passives.
- Ancestral Endurance: `max_endurance_charges` raises `virtual_maximum_endurance_charges` (3 + 2), which the client
  shows and `Charges.LimitsOf` caps gains at.
- Toughened Skin, action speed: `PlayerElementalAilments` floors its action factor at 1, so chill and freeze slow and
  stop nothing (the wiki's Action speed page treats Freeze as 100% less action speed).
  A passive change reconciles an ailment that is already running.
  The client bounds `intermediary_combined_action_speed_+%` below by 0 from the same raw stat (`0x14281c510`), and
  the server sends 0 for it.
- Toughened Skin, movement speed: after flasks and auras, `MovementVelocityPct` is at least 0 (PoB CalcDefence
  `max(MovementSpeedMod, 1)`).
  The walk estimate and the sent `movement_velocity_+%` follow it.
  The client bounds `movement_velocity_+%` below by 0 instead of -100 from the raw flag (`0x14251cbd0`, checked
  against the disassembly), so both sides agree.
- Toughened Skin, body armour: PoB reads the line as `Armour MORE 100` for the Body Armour slot, so the body armour's
  local armour counts twice after the Ward conversion, before the global increases.
  With Iron Reflexes the body armour's evasion, converted to armour under the same modifier, counts twice too.
  The client's own calculator for `physical_damage_reduction_rating` (`0x142731060`, registered at `0x1400b82e0`)
  applies the same `(1 + flag)` factor to the body armour share, its converted evasion included.
  The Defence tab is expected to show the server's sent final rating rather than doubling it again; check it live.
- Ancestral Defiance: once an enemy hit is decided not evaded, before it is rolled, Life gains 10% of
  (maximum Life - Life), rounded down, only while alive (PoB CalcDefence).
  Life reservation is not modelled, so unreserved Life is maximum Life.
  A blocked hit counts: the wiki's Blocking page says blocking does not prevent the defender's when-hit effects.
  Whether the game treats "before being Hit" the same way for blocked hits, and its rounding, are unverified.

- Call of the Ancestors, speed: a warcry's use time is its GrantedEffects CastTime over
  `1 + warcry_speed_+% / 100` (the player's and the gem's) and over action speed, at least one server tick
  (`WarcryCastSeconds`, PoB `calcWarcryCastTime`; Enduring Cry 0.8 s becomes 0.267 s).
  The client times the cry from the same CastTime (row offset `+0x5f`) and `warcry_speed_+%` at `0x141abcaa0`
  (checked against the disassembly); it applies action speed to the actor's animation itself.
- Call of the Ancestors, cooldown: warcries now use the generic one-use cooldown (`SingleUseCooldown`), whose
  recovery for a Warcry-type skill adds `warcry_cooldown_speed_+%` (PoB: cooldown recovery for the Warcry type).
  Enduring Cry's 8 s recovers at 70%, and its skill record carries `virtual_cooldown_speed_+%` -30.
  The client's own virtual (`0x14281dfa0`) adds the same stat when the skill has `is_warcry`, so both agree.
  A spell's or attack's recovery is unchanged.
- Deathrattle runs at every warcry release (`WarcryReleased`), within the warcry's own radius around the player:
  the client's warcry action (`0x141abd120`) scans that radius for Chest objects and corpses when
  `nearby_corpses_explode_dealing_%_maximum_life_physical_damage_on_warcry` or `open_nearby_chests_on_warcry` is set.
  Only Enduring Cry has an executor, so only it explodes corpses and opens chests so far.
  - Corpses: a monster killed in this area visit, at zero Life, whose Life type allows corpse use and that no
    explosion has consumed (`SpawnedMonster.CorpseConsumed`).
    Each explodes once, is removed (S→C 0x2bb) and hits every living hostile monster within 22 cells of it (PoB's
    `EnemyExplode` radius, grown by area of effect; the game's radius is unverified) for 12% of its maximum Life as
    physical damage.
    The hit takes the player's `damage_+%`, `physical_damage_+%`, `area_damage_+%` and Frenzy Charge more damage
    (poewiki Explode: global stats apply, attack and spell ones do not), then the monster's mitigation.
    It is neither an attack nor a spell, so it is not blocked or evaded and it does not crit (0% base).
    Kills give experience, loot and quest progress through `MonsterKilled`.
    Not modelled: conversion, added damage and other global hit modifiers on the explosion, on-hit effects, the
    explosion art and the client's wave delay (distance / radius × 0.22 s in `0x141abd490`).
  - Chests: the Twilight Strand support gem chest (saved `Tutorial.ChestOpened` first, as when walked up to) and the
    Mud Flats Rhoa Nests (`InstanceStore.OpenChest`) open when they are within the radius.
    The server has no other chests yet.
    The wiki's Deathrattle page says warcries also open reward chests and strongboxes; that they open these quest
    chests is consistent with it but unverified.
- Ancestral Reach: `melee_attack_number_of_spirit_strikes` 2 and `extra_target_targeting_distance_+%` 50.
  Each non-Vaal melee Strike use (Heavy Strike and the other Strike gems, and the default attack) also hits 2 living
  hostile monsters 10 to 45 cells from the player, other than its target.
  The server picks them as the client does: the candidates in id order, shuffled by the client's generator seeded with
  the use's sequence and the player's id ([11](../protocol/11-skills.md) section 10d).
  Each extra hit is a full hit of the skill; a use grants one Fortification and one Rage gain.
  Not mirrored: the client's line-of-sight test and its choice among more than 20 candidates.
- Ancestral Echo: `melee_strike_skill_strike_previous_location` 1, with a melee weapon.
  A Strike skill's next use, from more than 5 cells away, also strikes where its previous use landed: a gem Strike hits
  the monsters within its radius there, the default attack the monster nearest it within reach.
  Each skill's location is kept until death; a use that hits a monster with its own swing does not hit it again there.
  The server sends the client's `skill_location_memory` buff (BuffDefinitions 1451) at the skill's first use.
  The client code that records the locations was not found, so whether the client draws the strike at the previous
  location needs a live check; the target rules there are the server's own.
- Born under Fire: BuffTemplates 674 gives every living hostile monster within 60 cells of the living player
  BuffDefinitions 2552 (`base_physical_damage_%_to_convert_to_fire` 50), added and removed as they move every 100 ms.
  A monster's hit within 60 cells at contact converts half its physical damage to fire, before mitigation, by the
  player's own conversion rules: fire resistance applies, and the fire can ignite on a critical hit.
  The generic passive enemy-aura path is [11](../protocol/11-skills.md) section 10e.

## Admission

The default Phrecia gate stays closed.
`Game_VERIFIED_ANCESTRAL_COMMANDER=1` admits only this identity after the same read-only installed-graph check as
Gambler (`VerifiedGamblerGraph`, ordinary `82D82CD3…` and combined `F2766B5A…`).
A client with the original Phrecia graph is refused before any save. Nothing installs or patches the archive.

With the graph installed, start through `scripts/dev/live.ps1` and run:

```text
/ascendancy phrecia Ancestral Commander
/ascendancy_points all
/add_passive 9828 61282 65410 40762 11217 37852 26496 32105
/ascendancy
```

For the defensive nodes, use `/add_passive 11217 37852 24701 23061 8455 848 26496 9828` instead.
The first build carries the strike and aura nodes: with Heavy Strike in a one-handed mace, a strike into a pack also
hits two monsters up to 45 cells away, and a second strike from elsewhere hits where the first landed (watch for the
client's own strike there).
Monsters within 60 cells carry the Born under Fire debuff, and their hits on the player show fire damage.
For the warcry nodes, use `/add_passive 41978 48740 29654 3245 24701 23061 8455 848` with an Enduring Cry gem:
the cry plays in a third of its time and its bar cooldown lasts about 11.4 s; killed monsters within 60 cells
vanish at the release and hurt living ones beside them; in The Mud Flats a cry beside a Rhoa Nest opens it.
Check the Defence tab's armour with a Plate Vest, the Charges tab's five Endurance Charges, walking speed with a
reduced-movement body armour, a cold hit's chill, and Life before and after a monster hit at half Life.

Coverage: `AscendancyGatewayIntegrationTests.AncestralCommander` and `VerifiedGamblerGraphTests`.
