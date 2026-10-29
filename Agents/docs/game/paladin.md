# Paladin

Paladin is the Duelist's Phrecia Champion identity (`Champion`, ascendancy row 11, 3.29.3b), start node 24984.
The start is implicit and free and has no stats; its link to 50986 is the ordinary Duelist start, not a class node.
The class is not complete yet. The small nodes and Anointed Champion work; the other notables need engine mechanics
that do not exist yet (passive-granted skills, debuffs on monsters, triggered skills, owned minions, link skills).

## Node checklist

Effect text and numbers are the wiki's (from the game's stat descriptions) and the 3.29.3b PassiveSkills values.

| Node | Effect | Gameplay consumer |
|---|---|---|
| 2134, 49271, 363, 29126, 54067, 43835, 2437, 38874, 11127 | Regenerate 0.5% of Life per second; 5% increased effect of Non-Curse Auras from your Skills | Done. `life_regeneration_rate_per_minute_%` 30 goes into the life regeneration the recovery loop applies (`PanelStats.LifePerMinute`); `non_curse_aura_effect_+%` 5 goes into `Pools.AuraEffectPct`, which scales every running aura's bonuses (`AuraCatalog.Bonus`, `RawStats`, `ResistanceStats`). |
| 52104 Anointed Champion | Consecrated Ground around you while stationary; its effects linger for 4 seconds; 50% increased effect of Consecrated Ground you create | Done; see below. Curse effect on self has no consumer: nothing in the engine curses the player. |
| 13763 Light of Divinity | Nearby (6 m) enemies take 25% increased Elemental Damage (BuffTemplate 672) | Not implemented yet: the engine applies no passive buff to monsters and has no per-monster damage taken from the player's debuffs. |
| 40299 Elemental Valiance | 25% chance to trigger level 20 Summon Elemental Relic on your or a nearby ally's kill, or on hitting a Rare or Unique enemy | Not implemented yet: no triggered skills and no owned minions. |
| 46492 Bring the Battle | Warcries have infinite Power; enemies taunted by you take 20% increased damage and deal 20% less damage to other targets | Not implemented yet: taunt has no gameplay effect and warcry power exists only inside Enduring Cry. |
| 42396 Field Medicine | Magic utility flasks applied to you have 40% increased effect; non-unique utility flasks you use apply to linked targets | Partly: the 40% is read for magic flasks (`UtilityFlasks`); there are no link skills to carry flasks to a target. |
| 40171 Absolution | 40% increased effect of Non-Curse Auras while you have a linked target; curses go to you instead of linked targets | Not implemented yet: no link skills; no curse source exists for the redirect. |
| 34448 Deep Connections | +5 to level of Link Skill Gems; no experience lost when a linked target's death kills you; link skills can target damageable minions | Partly: the +5 reaches the link gems' effective level (`SkillGemLevel`), but link skills have no executor; the engine has no death experience penalty. |
| 44795 Paladin Aura | Choice shell: one paid point, one free option | The allocation and refund rules work (`AscendancyProgress`); the options below grant nothing yet. |
| 58846 Determination | Grants level 30 Determination (GrantedEffectsPerLevel 17772) | Not implemented yet: skills come only from socketed gems and the default attack. |
| 28569 Grace | Grants level 30 Grace (GrantedEffectsPerLevel 17460) | Not implemented yet, as Determination. |
| 39868 Discipline | Grants level 40 Discipline (GrantedEffectsPerLevel 17316) | Not implemented yet, as Determination. |

Three legal eight-point builds, with a swap of the Paladin Aura option to Discipline, reach every node:

```text
363 13763 29126 40299 2437 52104 43835 46492
38874 44795 58846 2134 40171 49271 34448 54067 42396
54067 42396 11127 34448 49271 40171 38874 44795 28569
```

No legal build holds all nine small nodes: three sit behind notables, so eight points reach at most seven
(`363 2437 38874 43835 54067 40171 49271`, 3.5% of Life per second and 35% aura effect).
Refunding a Paladin Aura option costs five refund points; the shell itself refunds for free.

## Anointed Champion

- **Stationary.** The walk has ended (`now >= Walk.ArrivesAt`, the same test as the charge gains), the player is alive
  and not in a town (hideouts are not towns).
  While stationary, one consecrated ground of the stationary radius (18 cells, 1.8 m) stays where he stands, with the
  Sulphur Flask's ground art; it is removed (0x2bb) when he moves or dies, and a new one is made where he stops.
- **Radius.** As the client derives `virtual_consecrated_ground_while_stationary_radius` (0x1428d43a0): the plain stat,
  the Strength variant when Strength is the highest attribute (ties count), the Crusader variant with two or more
  Crusader items; the largest wins. No area modifier applies.
- **Linger.** The ground's effect stays for `your_consecrated_ground_effect_lingers_for_ms_after_leaving_the_area` after
  the last moment any of the player's grounds covered him, so leaving the ground and the ground ending both linger.
  Every ground the engine makes is the player's own (the Sulphur Flask's too). A death ends the linger.
- **Effect.** `consecrated_ground_effect_+%` raises the ground's stats: 5% of life per second becomes 7.5% (300 to
  450 per minute) and the curse effect on self -50% becomes -75% (Path of Building CalcPerform: `5 * effect` and
  `-50 * effect`). The regeneration adds to the small nodes', Fortification's and every other source's.
- **Where it runs.** `FlaskGrounds.State` keeps the radius and linger from the stats last sent to the client
  (`PlayerStatsWithCleared`); `RunPlayerTick` starts the flask-ground ticker when a radius is present, and the ticker
  keeps running while it is. `FlaskGroundsTick` does one 100 ms step. A new area has a fresh state.

Client evidence (decompile dump, checked against the disassembly): the client derives the radius at 0x1428d43a0 and
the linger at 0x142890140 (`0x230d`, plus `0x2461` while affected by Zealotry); both are registered virtual-stat
derivations (0x1400e5c70, 0x1400db900) with no other direct reader in the dump. The client does not predict the
ground or the linger; it shows the server's effect object and the `ground_consecration` buff (208).
`consecrated_ground_effect_+%` (0x23f8) has no direct client reader.
BuffDefinitions row 1262 `consecrated_ground_while_stationary` is invisible and has no stats; it is not sent.

Unverified: whether the game makes the stationary ground in towns (the engine does not, as it runs no auras there),
and whether the real ground disappears at once when the player moves (the linger makes the gameplay the same).

## Admission

The default Phrecia gate stays closed.
`Game_VERIFIED_PALADIN=1` admits only this identity after the same read-only installed-graph check as Gambler
(`VerifiedGamblerGraph`, ordinary `82D82CD3…` and combined `F2766B5A…`). A client with the original Phrecia graph is
refused before any save. Nothing installs or patches the archive.

With the graph installed, start through `scripts/dev/live.ps1` and run:

```text
/ascendancy phrecia Paladin
/ascendancy_points all
/add_passive 363 13763 29126 40299 2437 52104 43835 46492
/ascendancy
```

Stand still: the consecrated ground appears around the player and the Defence tab shows 9.5% of Life regenerated per
second (four small nodes and 7.5% from the ground). Walk away: the regeneration stays for 4 seconds, then drops to 2%.

Coverage: `AscendancyGatewayIntegrationTests.Paladin`.
