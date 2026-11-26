# Legion: Timeless Monoliths, Scarabs, Atlas and the Domain

Phases 1-4 of the Legion league mechanic: Timeless Monoliths, timer/alert stasis armies, break-out, release, war chests,
rewards, Legion Scarabs, Atlas passives (including `Timeless Conflict`), and the Domain of Timeless Conflict.
Module `Modules/Core/Content/Legion` (`LegionMonolith`, `LegionDomain`, `LegionArmy`, `LegionStasis`, `LegionRewards`,
`LegionRules`, `LegionData`); the area roll that puts the monolith room in a map is in [league-encounters](league-encounters.md).
The rules the code follows are the wiki's for 3.29; what the client data and code say is below, marked as a guess where it is one.

## Client objects and states

- `Metadata/Terrain/Leagues/Legion/Objects/Legion_EncounterLocator` is only a marker (`server_only`) at the centre of the
  room `legion_monolith.arm`. The server creates a clickable `LegionInitiator` at every matching locator in the area (supporting
  multiple monoliths when scarabs or mods add extra rooms); no formation file exists in the client, so where each monster stands is the server's.
- The campaign's monoliths are not the league-chance roll (`WorldAreaLeagueChances` gives Legion 0 there): fixed locators sit
  in the areas' own rooms, `FirstTimeLegion_EncounterLocator` in The Climb (1_1_6, `flag_hill_1.arm`, sets `ClimbLegionActivated`)
  and `DriedLake_EncounterLocator` in The Dried Lake (1_4_2, `nightwane_boss.arm`, sets `DriedLakeLegionActivated`).
  `LegionEndgame_EncounterLocator` belongs to the Domain of Timeless Conflict (`LegionDomain.cs`).
- `LegionInitiator` StateMachine drives all 7 shared states (`obelisk_state`, `charging`, `shattered`, `crystal`, `spawn_lightning`, `locked`, `active`) across `Idle -> Charging (0,1,0,0,*,1,1) -> Running (2,1,1,1..3,*,1,1) -> Frozen (3,1,2,0,0,1,1) -> Battle (4,1,2,0,0,1,0) -> Ending/Finished (5,1,2,0,0,1,0)` (official 3.29 captures). Clicking also sends `0x2bc` `InteractionAction` `[0]`. When a General spawns, base-data property `0xec72e38c` links its entity id onto the monolith (`0x2c1` live update + `0x2bb` for late joiners) so `spawn_lightning=1` aims bolts towards the General while frozen, and the formation axis aligns the General's army with the monolith's forward rotation.
- `LegionFlashLight` (`Metadata/Terrain/Leagues/Legion/Objects/LegionFlashLight`) is created alongside each monolith with base-data properties `0x40f0fc9c` and `0x7700c879` = monolith id, `Positioned` `flags0 = 0x02` (parent lookup), and `StateMachine` (`foggy`); at the shatter, `0x2bc` `Animated` `[0]` triggers the flash.
- Every Legion monster and war chest has the states `present`, `breakout`, `activated`, `owning_initiator` (generals also `general`, `fleeing`, `disappear`).
  The crystal is part of the monster's own model, so a new monster is drawn encased with nothing sent.
  Hidden is `present=0, breakout=0, activated=0, owning_initiator=<monolith>`; revealed is `present=1`; broken out is `present=1, breakout=1` (crystal off, freed glow); fighting is `present=1, breakout=1, activated=1`.
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
- `LegionFactions.MiscAnimatedKey1` (break-out, `breaking_free.ao`) and keys 2, 3 and 5 are played by the client from the action (state 3), no longer by an effect object.
- Area environment overrides (S->C `0xdf`, `Environments` rows looked up by `Id` so row shifts across builds do not break): `LegionLeagueWorld` (`0.5` s in at shatter, `-2.0` s out at freeze), `LegionLeagueWorldBattle` (`0.0` s in at release, `-2.0` s out `8/30` s into Ending), and `LegionLeagueWorldPostBattle` (`0.2` s in at Ending, `-2.0` s out `76/30` s into Ending). Multi-monolith instances guard fade-outs while another monolith is still active.

## The Legion tables

| Table, column | Meaning | Source |
|---|---|---|
| `LegionBalancePerLevel` col3 | the timer, in ms; the row is the one with the highest `MinLevel` <= area level | client code |
| `LegionBalancePerLevel` col2, col4, col15 | pre-timer ms (0), fall-off ms (500), `Timeless Conflict` alert gauge cap (1000) | client code |
| `LegionBalancePerLevel` col1, col5 to col14 | unknown (col5 and col6 are copied by the client and never read) | not read |
| `LegionFactions.SpawnWeight`, `LegionBalancePerLevelKey`, `StatsKey`, col3 | faction draw weight, minimum area level, `map_legion_monoliths_chance_for_*` stat, formation orderliness (`0.95` grid vs `0.65`/`0.50` loose) | table data, fits wiki |
| `LegionRanks` col0, col1..col4, col6, col7, col8 | tier (0..3), rarity weights, endless revival cooldown ms (`6000/6000/16000/24000`), general/hoard chance, alert value (`5/18/40/350`) | table data + guess |
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
The ruleset `LegionRules` (Rulesets 94) is added via `0x1f` (and via `GameEvents.EnterMessages` before area objects for late joiners) and driven with S->C `0x2c9` payloads: mode 0 (37 bytes including monolith angle and timer duration), mode 1 (`0x01`, activated at shatter), and mode 5 (`0x05`, stage/step `1,1 -> 1,2 -> 1,3 -> 2,4 -> 2,5`).

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
- The openable chest inherits `Metadata/Chests/LegionChests/LegionChestBase` (8 serialized components including an empty `StateMachine`, `Animated = 0` so the client loads `Chests.AOFiles[0]`). While closed it displays the minimap icon stat and links its primary floating 3D reward symbol (`EffectObject` at capture height `25 / -195.3125f` and `LegionRewardTypeVisuals.__col4` scale) via base-data variable `0xf3a80181`; opening the chest removes all floating symbols. No client table links a reward type to items: what a type drops is the server's own
  (`LegionRewards.cs`, the row -> kind table). Incubators (row 17) no longer drop in 3.29, row 11 repeats row 10 (Essences), and endgame symbols are level-gated (Maps/Scarabs L68+, Fragments L36+, league pools L30+) so campaign monoliths do not roll empty symbols.
- Pool rules: Abyss rolls Magic/Rare Eye Jewels and Rare Stygian Vise with its Abyssal Socket (never white 0-mod jewels); Harbinger drops Shards plus rarer full orbs (no Beachhead unique scrolls); Fragments uses an explicit weighted allowlist (Sacrifice, Offering, Vessel, Mortal, normal Legion Emblems, Elder/Shaper/Maven); Essences excludes all Deafening and corruption-only tiers; Uniques uses `IsDropEligibleUnique`; Maps uses `RollMapDropForKill`; Scarabs passes `MapModsOf`; Divination mixes Divination Cards and Stacked Decks; Gems drops Superior/Vaal gems and occasional GCP; Ruthless scales counts and splinters by `LeagueRules.ItemDropMultiplier`.
- A sergeant's reward chance is the level's `LegionBalancePerLevel` `__col1` (100 to level 9, down to 50 from level 41) plus stat 13951; what that column means is a guess.
- Every chance and count of the rewards (who carries one, how many, items per reward, splinters, the general's crystal, the pool weights) is a
  guess, a named constant in `LegionArmy.cs` and `LegionRewards.cs`. Splinters come only from sergeants, chests and the general, never normal soldiers. A map general drops no Timeless Jewel (`GeneralJewelPermille` is 0): they come from the Domain of Timeless Conflict.
- 100 splinters become an emblem through the generic `FullStack` row of the item, with no Legion code.

## Phase 4: Scarabs, Atlas passives and Timeless Conflict

- The scarab's extra monolith comes from the level generator: `CoreLeagues` 15 `ForcedSpawnStat` is
  `map_legion_league_extra_spawns`, which `ExtraTerrainFeatures.LeagueCount` adds to the base count, so no module code
  places it.
- Scarab, Atlas passive and Idol stats reach the encounter through `Instance.KeptAreaStats` (`LegionMonolith.AreaStats`).
- `atlas_keystone_timeless_conflict` / `map_legion_uses_alert_instead_of_timer`: no countdown timer, `LegionRules` mode 0
  with `alertMode = 1`, `LegionRanks.__col8` per breakout (5/18/40/350) toward `LegionBalancePerLevel.__col15` (1000).
  GUESS: the chest alert values 40/80. Live check: the alert gauge (`0x2c9` alert opcodes are not disassembled).

## Phase 3: Domain of Timeless Conflict

- `MapDeviceRecipes` 30-33 (`AtlasNode` 143 `LegionDomain`, `HASH16 = 50774`) map 2-5 distinct Timeless
  Emblems (`BaseItemTypes` 3055-3064, `MapFragmentFamilies` 15-19) to `WorldAreas` 1328-1331 (`LegionLeague`..`LegionLeague4`,
  no `MonsterPacks`, `Environments` 1177). Timer `15 + 60 * emblemCount` s, from `map_legion_endless_mode_+_s`.
  3.29.0 removed the Unrelenting Timeless Emblems ("existing items have been converted to regular Timeless Emblems"): the five
  Unrelenting rows count as their faction's regular emblem (its family and mods), with no uber Domain, drop or reward scaling.
  Area level (3.22.0): at least 80, +1 with 4 emblems, +2 with 5 (`WorldAreas` say 80; no level stat is in the emblem mods).
- The arena has `LegionEndlessInitiator` (`obelisk_state`, `checking_control_zone`) at `LegionEndgame_EncounterLocator`; quest flag
  `QuestFlags` 2770 `EnteredDomainOfTimelessConflict`. Reward tracker (3.29.0: 100 small crystals at the centre light up, one per 3
  reward icons, past 100 still counts): the server creates 100 `LegionDomainRewardCrystal` (`active`, server_only) and sets `active` 1 on
  one more per 3 reward units banked; a unit is one reward icon times (100 + the emblems' `map_legion_monster_reward_chance_+%`)/100,
  fractions kept; only newly lit crystals are sent. GUESS: their positions, a ring of radius 18 cells, evenly spaced (100 distinct cells).
- The Domain army fills the whole disc, not a near ring (R17/R18): only about 3 character lengths round the monolith stay clear. GUESS: the clear radius 18 cells; the arena radius is the one holding 90 % of the walkable cells within 300 of the centre (200 without a walk map); a squad's depth % maps onto [18, arena] and the radius is drawn by area (sqrt).
  The client runs the control zone itself while `checking_control_zone` is 1 (`0x141629580`): a player within 60 cells
  (grid distance squared below 3601) gets pack 0x1f `LegionEndlessControlZone` (`counter.epk`). Revival waves (3.29) need a player
  there: all ready sergeants and generals and half the ready soldiers. GUESS: the start radius of 28 cells, the (333, 333)
  fallback, and `map_legion_endless_monolith_pulse_time_ms` (20000) as the shortest time between two waves.
- No client code creates `MiscAnimated` 3337 `LegionControlZone` (`endgame/control_zone.ao`) or 3338 `LegionControlZonePerimeter`
  (`standhere.ao`): the server places both at the centre until 00:00. `control_zone/rig.amd` clips: 0 `idle` (loop), 1 `idle_off`,
  2 `off`, 3 `player_in`, 4 `player_out`, 5 `revival_wave` (1333 ms, once), played with S->C 0x1b2 kind 0 (wave clip at the start
  and each wave, then idle).
- Monsters come from `LegionRanks` 56-59 (`MinLevel = 101`, `*Endless` Tier 1 varieties, revival cooldowns
  `6 s / 6 s / 16 s / 24 s`); the end `MiscAnimated` 3357.
- A soldier that does not fight is a ghost running the client's `LegionRevive` action (type 0xd59a, skill 0x159a; wire layout:
  [protocol 07 section 4.1](../protocol/07-latency-visibility-movement.md)); the client runs its timers and looks (`MiscEffectPacks` id = row + 1):
  state 4 ready (pack 0x1a `LegionEndlessActive`), 3 fallen (0x19 `LegionEndlessInactive`; plays the current clip from 0 slowing to a
  stop over 3 s, and at its timer turns to 4 by itself), 5 vanish (0x1b, 0.8 s; with revive 1 the client then stands it up: its own
  state 1, the death clip backwards for 1 s), 1 revived (by 0x2c8 or in a create; not by the client's own state 1: 0x1c `LegionEndlessRevived` and `MiscAnimated` 3377
  `beam_down.ao` at the monster), then 2. In state 4 with revive 1 and its timer out, the client poses it with `Animation` 6 (Death)
  at 0.95..1.0 of the clip (`0x141c4a8f0`): that is how a ghost created ready lies, since the server has no death clip index.
  The 0x1b pack is removed only by 0x2c8 state 1.
  GUESS: the fall's pose (0.7 of a 1.4 s death clip) and `virtual_is_hidden_monster` taking the hover off a hostile ghost.
- Per-revival stats of the emblems (suffix `_per_revival`), summed over the emblems used and times the revival count. Timeless
  (`Mods` 21228-21232), all with `uber_domain_monster_tankiness_+%` 10: Karui crit chance 50 and crit multiplier 5, Maraketh attack
  and cast speed 5, Eternal accuracy 10 and area of effect 5, Templar penetrate all resistances 3, Vaal phys-as-chaos 5.
  The Unrelenting rows' own mods (`Mods` 21238-21242, `maximum_life_+%` 10 and other stats) are not applied (3.29.0 removed them).
  GUESS: the cap of 10 revival stacks.
- The end at 00:00 (rewards on the ground around the centre) runs once, at the first tick of any player in the area after the timer, or
  when a player enters an area whose timer ran out while it was empty (it has no frames then); a dead player's tick runs it too.
  At the end the server sets the initiator's `obelisk_state` 5 (the `inactive` clip), lights what is owed and plays `explode` on all 100
  reward crystals (S->C 0x1b2 kind 0), and drops the rewards; it prints no chat summary (the real game has none; the log has one). `Environments` 1177 is the
  arena's own base environment, so the server sends no `0xdf` for it. GUESS: the `explode` clip index 2 (`SmallCentralCrystal01.ao` views
  unlit, lit_up, explode). Live check: the crystal's explode, the 3357 effect we play at the end (the real beams, `LegionSoulercoaster1..6`,
  are not known: not built), and the obelisk's `inactive` look.
- Generals' lines: `NPCTextAudio` rows `Legion<General><Trigger>N` (their `NPCs` column is the speaker; an `NPCs` row's Id is the general's
  `MonsterVarieties` Id, a variant such as `...GeneralFish` uses the longest prefix), sent as S->C 0x1c7 with object id 0 to the players in the
  area. Normal encounters: MapIntro at the reveal, Fleeing when an unbroken general flees, Defeat on death. Domain: MapIntro once per
  general, Defeat on death, BattleCry when a revived general spawns and Dismount when Aukuna's mount falls, one per Domain per 30 s. GUESS: the stem is the trigger,
  the 8 s start delay plus 5 s per further general, several Defeat lines at once 2.5 s apart; AttackN, Taunt and Injured are not played
  (no clean trigger); object id 0 (global) rather than the general's id.
- Aukuna starts mounted: `LegionMonsterVarieties` 24 plans 7754 (on foot, `MonsterMapBoss`, the loot) and its `MonsterVarietiesKey2`
  7753 is the mounted form (`MonsterNoDropsOrExperience`), spawned in stasis, woken and revived in the Domain (R18: she dismounts again
  there, an inference). The mount's death clip is `dismount_01`; the server plays its events (`LegionGenerals.cs`, R17): at `spawnbird`
  the bird 7756 (`Invisible`, NoAi; its .ot copies the mount's pose and pauses it) and `nobird` 1 on the mount (detaches its own bird),
  at `birdepk` the bird's `explodeepk` 1 (`DinoExplode.epk`), at `phase` 7754 where the mount fell; the mount's corpse goes (its own
  `disappear` event already made it invisible). The fighter entry (drops, Defeat line), the encounter's released id and the Domain slot
  pass to 7754, which says a Dismount line (in the Domain through the 30 s chatter). The mount's own `use_arrow2` default is 1.
  GUESS: the event times 1.73, 1.945 and 2.64 s (names only; the server has no clip clock), the bird going 2 s after the phase; the
  bird's `DinosaurExplosion` / `LandingBurningGround` hits are not played. Live check: does the bird copy the pose without a
  `reference` object, and does the client play `dismount_01` from the mount's ordinary death.
- Hyrri is two objects (R17): the plan's 7704 `LegionKaruiGeneralFish` (targetable, the loot, the travel kit; `MovementSpeed` 0) and the
  rider 7702 `LegionKaruiGeneral` (.ot: untargetable, `base_cannot_be_damaged`, no drops, non-blocking, immobile), spawned on the Fish
  when it wakes or is revived; the server keeps hits, minions and rewards off the rider, removes it with the Fish, and holds the
  rider's own teleports (10063/10064) so the Fish is the one that moves. The Domain slot, ghost and revival are the Fish's.
  GUESS: no client link between the two (the server teleports the rider onto the Fish's cell, at most every 0.2 s); a water clone
  7703 (NoAi, `dying` state) where the mortar 9988 lands is placed near the rider's target 1 s after it fires (the server does not
  follow the mortar), lives 8 s, at most 3 per Hyrri. Live check: does the rider ride the Fish or float beside it, and does the
  frozen Fish (no rider in stasis) look right.
- Not yet built: the `LegionCocoonCrystal` item-insertion UI window for Enshrouding Crystals (requires client protocol RE).
