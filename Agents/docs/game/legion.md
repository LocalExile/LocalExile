# Legion: the Timeless Monolith encounter

Phase 1 of the Legion league mechanic: the monolith, the timer, two armies in stasis, the break-out and the release.
Module `Modules/Core/Content/Legion` (`LegionMonolith`, `LegionArmy`, `LegionStasis`, `LegionData`); the area roll that
puts the monolith room in an area is in [league-encounters](league-encounters.md).
The rules the code follows are the wiki's for 3.29; what the client data and code say is below, marked as a guess where it is one.

## Client objects and states

- `Metadata/Terrain/Leagues/Legion/Objects/Legion_EncounterLocator` is only a marker (`server_only`) at the centre of the
  room `legion_monolith.arm`. The server creates the clickable `LegionInitiator` there; no formation file exists in the
  client, so where each monster stands is the server's.
- The campaign's monoliths are not the league-chance roll (`WorldAreaLeagueChances` gives Legion 0 there): fixed locators sit
  in the areas' own rooms, `FirstTimeLegion_EncounterLocator` in The Climb (1_1_6, `flag_hill_1.arm`) and
  `DriedLake_EncounterLocator` in The Dried Lake (1_4_2, `nightwane_boss.arm`). The module places the monolith at these and at
  `Legion_EncounterLocator`; `LegionEndgame_EncounterLocator` is the endgame's and is not used. One monolith per area: the
  first locator found.
- `LegionInitiator` StateMachine states: `charging=1` (build-up, untargetable, no collision), `shattered=1` (shatter, then idle),
  `shattered=2` (at the end: the crystal leaves, minimap icon hidden) with `locked=1` (untargetable, at the end), `spawn_lightning=1` (bolts from the monolith towards the general while it stands frozen).
- Every Legion monster and war chest has the states `breakout`, `activated` (generals also `general`, `fleeing`, `disappear`).
  The crystal is part of the monster's own model, so a new monster is drawn encased with nothing sent.
  Frozen is `breakout` 0, `activated` 0; broken out is `breakout` 1 (crystal off, freed glow); fighting is `activated` 1.
  A general still frozen carries the about-to-flee effect; `fleeing=1` plays its flee animation.
- Stasis is the client's own `LegionBreakout` action (action type 0xd8fe, skill 0x18fe), created with the monster (Actor current action) in state 0:
  hidden, no collision and no hits on the client and the server (`noBodyUntilReleased`). At the shatter a 0x2c8 state 2 reveals each member, nearest the
  monolith first over `RevealSeconds` (a guess), and the client poses it mid-attack in the attack clip the server names; state 3 at the break-out, state 5 at the end;
  a joiner is created in state 0 (the origin) and gets a 0x2c8 state 2 like the others (a broken-out member is created in state 3 and gets none). Protocol docs follow the live check.
- Buff 1311 `frozen_in_time` (flags: cannot be knocked back, minions take no actions) is NOT sent for the first live test of the action
  (`LegionStasis.SendFreezeBuff` = false, with the paused animation); the text below describes what the flag restores.
  Its default visual 1082 `legion_petrify` draws big purple spikes; the server sends it with the faction's visual as the
  AddBuff override (BuffVisuals 1099 Karui, 1100 Maraketh, 1101 Vaal, 1102 Templar, 1103 Eternal; Faridun uses the Maraketh one, a guess).
- The client keeps no damage for the stasis: `GameEvents.OutgoingDamage` zeroes a hit on a frozen member and lowers its life on the server instead (the area writes the new life to every life bar).
- A broken war chest (`LegionChestTypes`: the chest monster, and the `Chests` row it becomes) is replaced by that chest object.

## Effects (MiscAnimated)

- 3270 `LegionMonolithPulse`: the ring when the monolith shatters.
- 3326 `LegionChestBreakoutExplode`: a war chest breaking out.
- `LegionFactions.MiscAnimatedKey1` (break-out, `breaking_free.ao`) and keys 2, 3 and 5 are played by the client from the action (state 3), no longer by an effect object;
  `LegionFlashLight` at the shatter is not sent (no known trigger).

## The Legion tables

| Table, column | Meaning | Source |
|---|---|---|
| `LegionBalancePerLevel` col3 | the timer, in ms; the row is the one with the highest `MinLevel` <= area level | client code |
| `LegionBalancePerLevel` col2, col4, col15 | pre-timer ms (0), fall-off ms (500), alert gauge maximum | client code, not used here |
| `LegionBalancePerLevel` col1, col5 to col14 | unknown (col5 and col6 are copied by the client and never read) | not read |
| `LegionFactions.SpawnWeight`, `LegionBalancePerLevelKey`, `StatsKey` | faction draw weight, the first level it appears at, its `map_legion_monoliths_chance_for_*` stat | guess, fits the wiki |
| `LegionRanks` col0 | tier: 0 normal, 1 elite, 2 sergeant, 3 general | guess |
| `LegionRanks` tier 3 col7 | percent chance of a general (0 below level 46, 1 at 46, up to 8 at 82) | guess |
| `LegionMonsterCounts` col2 to col6 | min and max count, the depth band in percent, the number of varieties | guess, server-only table |
| `LegionChestCounts` col2 to col5, col7 | min and max chests, the depth band, War Hoard percent (tier 1 row) | guess, server-only table |
| `LegionMonsterVarieties` | the monsters of a faction and tier | client code reads it for the break-out |

The client has no `LegionMonsterCounts` or `LegionChestCounts` loader, so the count, chest and rank columns can only be
settled by comparing with the real game.
The code that reads them is `LegionArmy.Compose`; a correction goes there.

## The timer

`0x1416290a0` (the encounter constructor) sets the timer to `(col3 / 1000 + stat 9772) * (100 + map_legion_duration_+%) / 100`
seconds; stat 9772 is the endless mode's, ignored here. The row is picked by `0x141eb5c00`: the highest `MinLevel` <= level.
So 30 s at level 1, 28 s from 15, 15 s from 68. The server sends it with S->C 0x163 type 4 (`InstanceTimers.MissionTimer`).
The ruleset `LegionRules` (Rulesets 94) reads the same duration from the S->C 0x2c9 payload (mode 0 creates the encounter, mode 5 sets active and state).

## Open questions for the live check

- `LegionRules` (0x1f row 94, then 0x2c9 mode 0 at the shatter, mode 5 at the end) is sent with 0x163: does the HUD and the area audio 3355 start?
- The formation constants (`LegionFormation.cs`: front gap, body gap, area per member, width over depth, facing rotation) and the chest break-out radius are guesses.
- Tier rarity (elite Magic, sergeant Rare, general Unique) is a guess.
- Is the swap of a broken-out monster for a fresh fighting one visible, and does a hit on a frozen monster show a number or a flinch?
- Does the joiner's life bar match the lowered life? At break-out the member's life is refilled to its maximum (the bar must not stay near 0: live test), so its bar disappears.
- Does the faction visual (1099-1103) draw a thin coloured coat on the soldier's own model (Faridun with the Maraketh one)?
- Does the animation stay paused after the buff is removed, and does a fleeing general play its animation?
- With two players, does the one who enters during the timer see the army frozen? The area gives a joiner the monsters
  (`JoinArea`, at the end of the joiner's first frame); their buff 1311 and 0x1b2 pause are sent to it alone at
  `PlayerEntered` (`LegionStasis.Present`). If `PlayerEntered` runs before that frame ends, these records (order 0) are
  written before the monster creates (order Last, `SharedMonsters.cs:299`; sorted at `Program.WorldOutput.cs:225`).
  A deterministic fix would be the frozen buff as an area buff (`player.Area.Buffs.Set`, given to joiners by
  `AreaBuffs.Join`), as the rarity glow is (`Monsters.cs:628`); not done yet.

## What the encounter does

- The monolith is used from 12 cells away; charging lasts 1.5 s (the build_up clip), then it shatters and the timer runs.
- The factions: in The Climb Eternal Empire and Karui, in The Dried Lake Eternal Empire and one random; elsewhere two are
  drawn by `SpawnWeight` and the `map_legion_monoliths_chance_for_*` stats, among those whose balance row allows the level.
- The army spawns frozen for every player in the area (`LegionFormation.cs`): a random axis through the monolith, one block per army
  on each side, facing the other; width about 1.75 times depth, sized from the member count. Spacing between centres is max(12, 4.5 x ObjectSize) cells in rows (jitter 0.3 of it) and
  max(13, 5 x ObjectSize) in loose groups, each member drawing a factor 0.85-1.4 for its minimum distance (ObjectSize is only the collision size; the coated soldier is drawn 6-8 cells wide); the fronts are
  1.5 spacings apart. Eternal Empire and Templar stand in rows and columns, the others in loose groups. The band percent is the distance from
  the front (0) to the rear (100): a member fills its band first and spills to the rows behind, then in front, only when the band is full.
  The general is placed first, centred at the rear, and falls back to the rear half (loose groups) or any free slot (rows). A blocked cell tries its neighbours, then the member is skipped.
- The campaign monoliths (The Climb, The Dried Lake) bring `CampaignArmyScale` = 0.5 of the planned normal and elite monsters (at least 1 per squad; sergeants, the general and chests unchanged). A guess from memory.
- Only the player's own direct hits count: each lowers the frozen monster's life and its life bar, and at 0 it breaks out and its life is refilled (the bar goes). Damage over time does not count (the frozen buff makes the monster invulnerable, and no general hook lowers an invulnerable monster's life).
  Totems, brands, traps, mines and minions do not count yet.
- A broken war chest breaks out the non-sergeant, non-general monsters near it and becomes an openable chest.
- The encounter ends at the timer, or early at 90% of the plain and elite monsters broken plus every chest, sergeant and general.
  Each broken member is then swapped for a new fighting monster, the others vanish, and an unbroken general flees.
