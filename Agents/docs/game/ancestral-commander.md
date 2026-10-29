# Ancestral Commander

Ancestral Commander is the Marauder's Phrecia Juggernaut identity (`Juggernaut`, 3.29.3b), start node 17765.
No node grants passive points.
The defensive nodes have server consumers with Gateway coverage; the warcry, strike, enemy-aura, Spirit and totem
nodes are still open, as the checklist says.
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
| 48740 Call of the Ancestors | 200% increased Warcry Speed, 30% reduced Warcry Cooldown Recovery Rate | Not yet. |
| 3245 Deathrattle | Nearby corpses explode for 12% of their Life as Physical when you Warcry; Warcries open Chests | Not yet: no corpse consumption or generic chests. |
| 40762 Ancestral Reach | Non-Vaal Strikes target 2 additional nearby Enemies, from 50% further away | Not yet: no strike targeting. |
| 61282 Ancestral Echo | Strikes also target the previous location they were used | Not yet: no strike targeting. |
| 32105 Born under Fire | Nearby Enemies convert 50% of their Physical Damage to Fire | Not yet: no passive enemy auras. |
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
Check the Defence tab's armour with a Plate Vest, the Charges tab's five Endurance Charges, walking speed with a
reduced-movement body armour, a cold hit's chill, and Life before and after a monster hit at half Life.

Coverage: `AscendancyGatewayIntegrationTests.AncestralCommander` and `VerifiedGamblerGraphTests`.
