# Legion: the Timeless Monolith encounter

Phases 1-2 of the Legion league mechanic: the monolith, the timer, two armies in stasis, break-out, release, war chests and rewards.
Module `Modules/Core/Content/Legion` (`LegionMonolith`, `LegionArmy`, `LegionStasis`, `LegionRewards`, `LegionData`); the area roll that
puts the monolith room in an area is in [league-encounters](league-encounters.md).
The rules the code follows are the wiki's for 3.29; what the client data and code say is below, marked as a guess where it is one.

## Client objects and states

- `Metadata/Terrain/Leagues/Legion/Objects/Legion_EncounterLocator` is only a marker (`server_only`) at the centre of the
  room `legion_monolith.arm`. The server creates a clickable `LegionInitiator` at every matching locator in the area (supporting
  multiple monoliths when scarabs or mods add extra rooms); no formation file exists in the client, so where each monster stands is the server's.
- The campaign's monoliths are not the league-chance roll (`WorldAreaLeagueChances` gives Legion 0 there): fixed locators sit
  in the areas' own rooms, `FirstTimeLegion_EncounterLocator` in The Climb (1_1_6, `flag_hill_1.arm`) and
  `DriedLake_EncounterLocator` in The Dried Lake (1_4_2, `nightwane_boss.arm`). The module places monoliths at these and at
  `Legion_EncounterLocator`; `LegionEndgame_EncounterLocator` is the endgame's and is not used.
- `LegionInitiator` StateMachine states: `charging=1` (build-up, untargetable, no collision), `shattered=1` (shatter, then idle),
  `shattered=2` (at the end: the crystal leaves, minimap icon hidden) with `locked=1` (untargetable, at the end), `spawn_lightning=1` (bolts from the monolith towards the general while it stands frozen; when a general is planned, the formation axis aligns the general's army with the monolith's forward rotation).
- Every Legion monster and war chest has the states `breakout`, `activated` (generals also `general`, `fleeing`, `disappear`).
  The crystal is part of the monster's own model, so a new monster is drawn encased with nothing sent.
  Frozen is `breakout` 0, `activated` 0; broken out is `breakout` 1 (crystal off, freed glow); fighting is `activated` 1.
  A general still frozen carries the about-to-flee effect; `fleeing=1` plays its flee animation.
- Stasis is the client's own `LegionBreakout` action (action type 0xd8fe, skill 0x18fe), created with the monster (Actor current action) in state 0:
  hidden, no collision and no hits on the client and the server (`noBodyUntilReleased`). At the shatter a 0x2c8 state 2 reveals each member, nearest the
  monolith first over `PulseSeconds` (0.791 s), and the client poses it mid-attack in the attack clip the server names; state 3 at the break-out, state 5 at the end;
  a joiner is created in state 0 (the origin) and gets a 0x2c8 state 2 like the others (a broken-out member is created in state 3 and gets none). Wire layout: [protocol 07 section 4.1](../protocol/07-latency-visibility-movement.md).
- Buff 1311 `frozen_in_time` (flags: cannot be knocked back, `base_damage_taken_+%`) is NOT sent while testing the action alone
  (`LegionStasis.SendFreezeBuff` = false, with the paused animation); `EncounterStats` still carries `CannotBeKnockedBack` and `map_legion_frozen_monsters_damage_taken_+%`.
  Its default visual 1082 `legion_petrify` draws big purple spikes; with the flag on, the server overrides it per faction
  (BuffVisuals 1099 Karui, 1100 Maraketh, 1101 Vaal, 1102 Templar, 1103 Eternal; Faridun uses Maraketh, a guess).
- Stasis damage runs through normal combat (`TakeDamageLocked`) with a non-lethal life floor (`SpawnedMonster.OnNonLethalZero`): hidden (state 0) and already-broken (state 3) members hold `EntranceGrace`, while revealed unbroken members (state 2) take hits, crits, resistances, armour, energy shield, damage over time (including Contagion/Essence Drain spread at the floor), minions, totems, brands, traps, mines and culling strike; life stops at 1 without triggering on-kill effects, breaks the member out, and refills life/ES so the client hides the bar.
- A broken-out war chest explodes (`LegionChestBreakoutExplode`), frees nearby tier 0-1 monsters, and stays as a broken-out silhouette (state 3, `breakout=1`, overhead reward icons preserved) until release (`00:00` or early end), when `ReleaseArmy` removes the stasis shell and spawns the clickable `Chests` object (`HardmodeChest` in Ruthless).

## Effects (MiscAnimated)

- 3270 `LegionMonolithPulse`: the radial shockwave at shatter and again at release (`00:00` / early end); removed once its clip ends so joiners do not replay it.
- 3326 `LegionChestBreakoutExplode`: a war chest breaking out.
- `LegionFactions.MiscAnimatedKey1` (break-out, `breaking_free.ao`) and keys 2, 3 and 5 are played by the client from the action (state 3), no longer by an effect object;
  `LegionFlashLight` at the shatter is not sent (no known trigger).

## The Legion tables

| Table, column | Meaning | Source |
|---|---|---|
| `LegionBalancePerLevel` col3 | the timer, in ms; the row is the one with the highest `MinLevel` <= area level | client code |
| `LegionBalancePerLevel` col2, col4, col15 | pre-timer ms (0), fall-off ms (500), alert gauge maximum | client code, not used here |
| `LegionBalancePerLevel` col1, col5 to col14 | unknown (col5 and col6 are copied by the client and never read) | not read |
| `LegionFactions.SpawnWeight`, `LegionBalancePerLevelKey`, `StatsKey`, col3 | faction draw weight, minimum area level, `map_legion_monoliths_chance_for_*` stat, formation orderliness (`0.95` grid vs `0.65`/`0.50` loose) | table data, fits wiki |
| `LegionRanks` col0, col1..col4, col7, col8 | tier (0..3), rarity weights, general/hoard chance, alert value (phase 4) | guess |
| `LegionMonsterCounts` col2 to col6 | min and max count, the depth band in percent, the number of varieties | guess, server-only table |
| `LegionChestCounts` col2 to col5, col7 | min and max chests, the depth band (tier 0 `20..70%`, tier 1 War Hoard `35..100%`), War Hoard percent (tier 1 row) | guess, server-only table |
| `LegionChestTypes` col0, `Chest`, `HardmodeChest` | frozen chest monster variety, release `Chests` row (standard and Ruthless) | table data |
| `LegionMonsterVarieties` col5, col6, col10..11, col15 | prop chance %, variety spawn weight (`__col6`), prop clip time range ms, prop count per `MiscAnimatedKey` (`__col15`) | table data + client code |

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
  on each side, facing the other; width about 1.75 times depth, sized from the member count. Spacing between centres is a minimum plus a factor of ObjectSize, in rows (with jitter) and
  in loose groups (the named constants of `LegionFormation.cs`), each member drawing a factor 0.85-1.4 for its minimum distance (ObjectSize is only the collision size; the coated soldier is drawn 6-8 cells wide); the fronts are
  1.5 spacings apart. Eternal Empire and Templar stand in rows and columns, the others in loose groups. The band percent is the distance from
  the front (0) to the rear (100): a member fills its band first and spills to the rows behind, then in front, only when the band is full.
  The general is placed first, centred at the rear, and falls back to the rear half (loose groups) or any free slot (rows). A blocked cell tries its neighbours, then the member is skipped.
- The campaign monoliths (The Climb, The Dried Lake) bring `CampaignArmyScale` = 0.5 of the planned normal and elite monsters (at least 1 per squad; sergeants, the general and chests unchanged). A guess from memory.
- Revealed frozen members take normal combat damage (hits, crits, resists, armour, ES, DoTs with Contagion spread, minions, totems, brands, traps, mines, culling strike) with a non-lethal floor at 1 life; at the floor the member breaks out, refills life/ES (hiding the bar), and does not trigger on-kill effects.
- Each monster's rarity is rolled in the plan from the `LegionRanks` rank row's col1..col4 as Normal/Magic/Rare/Unique weights (soldiers 90/10, elites 75/25, sergeants Rare, the general Unique); the meaning of the columns is a guess.
- A broken war chest explodes, breaks out the non-sergeant, non-general monsters near it, and stays as a broken-out silhouette until release, when it becomes an openable chest (`HardmodeChest` in Ruthless).
- The encounter ends at the timer, or early at 90% of the plain and elite monsters broken plus every chest, sergeant and general; the early end breaks out every monster still frozen (patch 3.29.0), which then fights.
  At either end a second pulse wave plays, each broken member is swapped for a fresh fighting monster spawned directly with `breakout=1, activated=1` and its preserved formation facing (`Fighters` persisted across reloads), broken chests become openable `Chests` objects, unbroken members vanish and an unbroken general flees.

## Rewards

- The symbol over a sergeant or war chest is buff 1328 `legion_reward_display`, its value list `[LegionRewardTypeVisuals row, hidden, height, index, count]`
  (always 5 entries; the client reads them unchecked in `0x1417c5d00`). Several rewards are several instances (one `Tag` each), `index` of `count`, side by
  side 60 units apart; `hidden` 1 removes the effect (`0x1415d1d50` registers the behaviour). The row is the table's row index, not `IntId`.
- The minimap icon is not part of the buff: stat 2494 `monster_minimap_icon` = the row's `MinimapIconsKey` + 1, read once when the client builds the entry
  (`0x142191100`), and stat 10006 `monster_hide_minimap_icon` non-zero hides it (`0x142191200`), so the server creates the member with both and sets 10006 to 0 at the reveal.
- The opened chest object has no Buffs component, so it shows only the icon. No client table links a reward type to items: what a type drops is the server's own
  (`LegionRewards.cs`, the row -> kind table). Incubators (row 17) no longer drop in 3.29, row 11 repeats row 10 (Essences), and endgame symbols are level-gated (Maps/Scarabs L68+, Fragments L36+, league pools L30+) so campaign monoliths do not roll empty symbols.
- Pool rules: Abyss rolls Magic/Rare Eye Jewels and Rare Stygian Vise with its Abyssal Socket (never white 0-mod jewels); Harbinger drops Shards plus rarer full orbs (no Beachhead unique scrolls); Fragments uses an explicit weighted allowlist (Sacrifice, Offering, Vessel, Mortal, normal Legion Emblems, Elder/Shaper/Maven); Essences excludes all Deafening and corruption-only tiers; Uniques uses `IsDropEligibleUnique`; Maps uses `RollMapDropForKill`; Scarabs passes `MapModsOf`; Divination mixes Divination Cards and Stacked Decks; Gems drops Superior/Vaal gems and occasional GCP; Ruthless scales counts and splinters by `LeagueRules.ItemDropMultiplier`.
- A sergeant's reward chance is the level's `LegionBalancePerLevel` `__col1` (100 to level 9, down to 50 from level 41) plus stat 13951; what that column means is a guess.
- Every chance and count of the rewards (who carries one, how many, items per reward, splinters, the general's crystal, the pool weights) is a
  guess, a named constant in `LegionArmy.cs` and `LegionRewards.cs`. Splinters come only from sergeants, chests and the general, never normal soldiers. A map general drops no Timeless Jewel (`GeneralJewelPermille` is 0): they come from the Domain of Timeless Conflict.
- 100 splinters become an emblem through the generic `FullStack` row of the item, with no Legion code.
