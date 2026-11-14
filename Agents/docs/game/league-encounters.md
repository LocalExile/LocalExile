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

Campaign and map defaults: Anarchy 5%, Torment 30% (then 12%), Essence 13% (then 10%), Ambush (strongboxes) 51% (then
18%), shrines 20% (then 10%). Maps: Torment 24/16, Essence 10/8, Ambush 43/12, shrines 16/8. The Twilight Strand and The
Coast have none of these.

## Anarchy: Rogue Exiles

RogueExiles.dat: `__col1` set on the 31 wild exiles (not the `Supporter`, Descent, Daresso, shade, map-boss and Faridun
varieties). `__col2` (5–88; none for Torr Olgosso, Jonah Unchained and the newest five) is read as their lowest area level:
it matches their known order (Orra Greengate 5, Igna Phoenix 6, Ash Lessard 7) but is unconfirmed. RogueExileLifeScalingPerLevel
gives their more life from level 68 (150%) to 87+ (1 860%). They are unique monsters. No Mods.dat row grants `is_rogue_exile`.

**Testing:** `Game_LEAGUE_CHANCES=Anarchy=100` (CoreLeagues ids, comma-separated) gives that chance in every area the
Gateway populates. `Game_ENCOUNTERS_AT_ARRIVAL=1` places each encounter about 12 cells from where the player arrives.
