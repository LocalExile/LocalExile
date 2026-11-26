# League encounters in areas

How many of a league mechanic an area gets, and where the server places it. Code: `src/GameServer.Gateway/World/LeagueChances.cs`
(the roll), `GameEvents.AreaPopulated` (the hand-over), `AreaPopulation.EncounterSpot` (the spot); one module per mechanic in
`Modules/Core/Content/`.

## The roll (client `0x141ea8800` → `0x141ea8cb0`)

The area's WorldAreas row points to two WorldAreaLeagueChances rows: `LeagueChance1` (+0x22a) and `LeagueChance2` (+0x23e,
used when world +0xd7 is set; the second rows are the `*HardMode` ones, so presumably Ruthless: not checked).
`0x141ea8800` reads each CoreLeagues row's chance from that row by byte offset (a 30-entry jump table at `0x141ea8ac4`):

| CoreLeagues | Chance | Repeat | | CoreLeagues | Chance | Repeat |
|---|---|---|---|---|---|---|
| 0 Anarchy | +0x8 | | | 15 Legion | +0x2c | |
| 1 Shrine | +0x24 | +0x28 | | 16 Blight | +0x48 | |
| 2 VaalSideArea | +0x60 | | | 17 Delirium | +0x30 | |
| 3 Ambush | +0x64 | +0x68 | | 18 Harvest | +0x34 | |
| 4 Beyond, 8 Harbinger | stats only | | | 19 Heist | +0x4c | |
| 5 Torment | +0xc | +0x10 | | 20 Ritual | +0x50 | |
| 6 Essence | +0x38 | +0x3c | | 21 Expedition | +0x54 | |
| 7 Breach | +0x40 | | | 22–26 | +0x5c, +0x70, +0x74, +0x78, +0x98 | |
| 9 Abyss | +0x44 | | | 27 Settlers | +0x9c | +0xa0 |
| 11–14 (Bestiary … Betrayal) | +0x88 … +0x94 | | | 28 Mercenaries | +0xa4 | |

The chance is then `chance × (100 + stat 0x3fd5) / 100 + stat 0x3fe5` (two area stats, 0 without map mods). One roll at
the chance, then rolls at the repeat chance while they succeed, up to CoreLeagues +0x8a (most per area, `__col11`).
+0x8e (`__col12`) needs a chance; +0xa0 (`__col15`) is never rolled here. Each roll is `0x14205b2a0`: at least 1, and 100
or `Next(100)` below it.

The level generator rolls only the mechanics that change the terrain (shrines, Legion, Blight, Breach, Harvest, Settlers:
[re/011](../re/011-level-generation.md) §3b). The others are the official server's, which is taken to use the same
function. Ours rolls them with its own generator after the packs (`AreaPopulated`), so a kept instance keeps what it rolled.
The Legion encounter that follows the Legion roll is in [Legion](legion.md).

Campaign and map defaults: Anarchy 5%, Torment 30% (then 12%), Essence 13% (then 10%), Ambush (strongboxes) 51% (then
18%), shrines 20% (then 10%). Maps: Torment 24/16, Essence 10/8, Ambush 43/12, shrines 16/8. The Twilight Strand and The
Coast have none of these.

## Anarchy: Rogue Exiles

RogueExiles.dat: `__col1` set on the 31 wild exiles (not the `Supporter`, Descent, Daresso, shade, map-boss and Faridun
varieties). `__col2` (5–88; none for Torr Olgosso, Jonah Unchained and the newest five) is read as their lowest area level:
it matches their known order (Orra Greengate 5, Igna Phoenix 6, Ash Lessard 7) but is unconfirmed. RogueExileLifeScalingPerLevel
gives their more life from level 68 (150%) to 87+ (1 860%). They are unique monsters. No Mods.dat row grants `is_rogue_exile`.

RogueExileGiants lists the gigantified exiles: each wild exile's "Supporter" variety of the same name, which the server
gives Mods MonsterSupporterGigantism1 (80% larger, 100% more life and area). The three "of the Afarud" varieties are
Faridun's. An exile's skills are its variety's GrantedEffectsKeys, which share their ActiveSkill rows with the players'
(Monster skills: `MonsterSkills.cs`).

The map stats the module reads (`Game.AreaLeagueStats`: the map's affixes, scarabs and fragments, and in a map the
allocated Atlas passives) come from the 3.29 sources still in the game: the Anarchy scarabs (`ScarabAnarchy1`–`4`:
5 exiles, 30% gigantified, 50% paired, 1 exceptional), the Atlas tree's `atlas_anarchy_*` wheel and Jumbled Abundance.
The leaguestone, watchstone and memory-line stats are read too, but nothing in 3.29 drops them.

Exile skills the client holds no area for: Fire Trap's ground, Bear Trap, Desecrate, Caustic Arrow's ground, Wither,
Flameblast, Rain of Arrows and the Vaal Cold Snap and Storm Call name no `active_skill_base_area_of_effect_radius`, and
their action classes add no constant to it (the ones that do: `SkillArea.ActionBaseRadius`), so their radius is server
data. Cremation's geyser (CorpseErruption, 3.29.3c 0x141adbae0) drops each projectile at a random angle and a distance
of 10 to twice the skill's radius from the geyser; it does not aim at anyone. Its projectile is a child action's
(vtable +0x540, 0x141310400), the `...Gore` row when `monster_projectile_variation` is set; the base row is unresolved.

## Abyss

From two official captures (a level 61 and a level 45 area, 72 crack creates). The pieces stand on a lattice of 125 world
units (11.5 cells; a cell is 250/23 units): the pit (`AbyssFinalNodeBase`) and each crack (`AbyssCrack`) carry the lattice
point in world units in Positioned (flag 0x40) and their MiscAnimated shape row in Animated (0x03 + u32). The client's
pieces were created one by one as the player walked up, at about 250 cells. An arm's first piece is two steps from the pit;
the pit's shape (`AbyssPitOne/Two/Three...`) names the directions its arms leave in. A crack is created open (Transitionable
8 with the 0x80 first-sight bit) and closes 9, 10, 11 one step every 16 frames from its far end; the pit is created closed
(1), opens (2) once the arms are closed and their 25 monsters dead, and is done (4) after the wave of 10-12. The plinth's two
properties (`0x40f0fc9c`, `0x7700c879`) carry the pit's id; the reward chest (`LeagueAbyss/FinalChestRareJewellery`, the
pit's cell) carries the area level in its Chest component and is ready 91 frames on (component 6 = 1, Transitionable 2).
Its open starts with S->C 0x1bd: chest, player, `9002`, `01 00 00`, player, then component 5 = `08 00 00 00` and
Transitionable 3, and drops 4 items (its row's `chest_item_quantity_+%` is 300). The captures never send `0x1f` AbyssRules.
The encounter belongs to the instance; the area's object records give a late joiner the pieces. Walking on a crack did not
start an arm; kills of monsters created 62 to 85 cells from a piece did, one created 91 away did not.

**Testing:** `Game_LEAGUE_CHANCES=Anarchy=100` (CoreLeagues ids, comma-separated) gives that chance in every area the
Gateway populates. `Game_ENCOUNTERS_AT_ARRIVAL=1` places each encounter about 12 cells from where the player arrives.

## Mercenaries (CoreLeagues row 28)

A map area rolls one mercenary of the Mercenaries of Trarthus at its WorldAreaLeagueChances chance (column +0xa4: 8% in
a map area, 0 in towns and the campaign's other areas). `LeagueChances.Count` returns 0 for the row (it is "not rolled"), so
`Mercenary` reads `ChanceOf` as the Delve sulphite does and adds the map's stats: `map_mercenary_league` forces it,
`map_disable_mercenary` and `map_disable_mercenary_from_chance` remove it, `map_mercenary_additional_chance_%` and
`map_mercenary_chance_+%` add to and scale the chance. The stat ids are the CoreLeagues columns' stored values plus one.
Of 221 official areas, 23 carry the Mercenaries rulesets; four of the five map ones hold exactly one mercenary, a unique
at the area's monster level.

The mercenary is the instance's. It waits friendly and scripted (team 70, `encounter_state` 1), a click opens the panel,
the panel's duel choice (a 0x2ca action of ruleset 161) starts a countdown of 54 frames, the friendly one is replaced by a
hostile one of its variety, and its death leaves the `*Allied` mercenary. The rulesets 161 and 162 are added by 0x1f when the
mercenary is placed or a player enters. Not built: the hire following and fighting for the player,
the duel opponent's drops, and the header listing of the two rulesets.

## Delirium scarabs

The five current scarabs use the existing map Mirror encounter and composed instance stats. Only the base scarab
guarantees a mirror. Mania scales reward-counter points and monster depth (capped at 100%); Paranoia adds two initial
types. Neuroses places Omniphobia and Kosis through the ordinary monster path and adds a distinct reward type per
boss killed. Their ids, spawn and reward flags survive module reload and reentry; repeated boss callbacks pay once.

Delusions prepares newly generated map drops before ground publication, including containers and encounter rewards.
The ground hook has no game rules. Player drops and restored ground objects bypass it; duplicate references to the same
generated item share its result. Unique maps and maps already carrying Delirium layers keep their original modifiers.
The enchant uses `AfflictionMapDeliriumStacks` and `AfflictionRewardMapMods`, with the reward formatter's one-based values.
It applies independently of whether the map contains a mirror.

Fidelity limits: the existing Mirror's fog, population, depth scaling and reward rates remain calibrated estimates.
Neuroses uses the existing boss kits and places both bosses in the first spawned group. Found maps draw 1–5 layers and
reward types uniformly; the client tables supply their identities and 20% layer size, not the retail distribution.
Permanent Delirium-map gameplay is a separate engine from this scarab's found-item enchant.
