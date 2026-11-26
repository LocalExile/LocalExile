# Blight & Blight-ravaged (Uber-Blight)

Server implementation of Path of Exile 3.29.3c Blight (`Modules/Core/Content/Blight/`), generic equipment-granted passive allocation (`Modules/Core/Game/Passive/Progression.cs`), and Reflective Oil routing (`StashRules.cs`).

## Architecture & data notes

- **Boss life formula (`BlightWaves.ComputeBossLife`):** `BlightBossLifeScalingPerLevel.MoreLife` is negative for area levels 73–84 (`-107` to `-1494`). In 3.29.3c it offsets `MonsterMapBossDifficulty.BossLifePercentIncrease`; summing both yields a monotonic curve from `+256%` at level 68 up to `+552%` at levels 81–85. Never apply `BlightBossLifeScalingPerLevel.MoreLife` standalone.
- **Area stats (`BlightEncounter.AreaStats`):** Reads `InstanceState.KeptAreaStats` first (then `MapModsOf`) so Atlas passives are never double-counted with `AreaLeagueStats`.
- **GGG `BlightTowers` table quirks (handled in `BlightTowers.cs`):**
  - Row 9 (`LightningStormTower`): `StatsKeys[1]` (`10654`) and `StatsKeys2[1]` (`10596`) are swapped relative to ring mod `21529` (`BlightLightningstormTowerRangePlusPct = 10596`).
  - Row 19 (`TemporalTower`): `StatsKeys` has 6 entries vs 5 in `StatsKeys2` (`12355` belongs to Stone Gaze).
  - Row 14 (`BuffPlayersTower`): references `17745` rather than `17747`.
- **Same-type lane resistance:** Cold, Lightning, Physical, and Fire lanes take `-99%` less damage and ignore control from matching tower families (`Mods 21478..21481`), bypassed by Freezebolt (`10585`), Shock Nova (`10591`), and Flamethrower (`10649`) ring anoints, and by Meteor Burning Ground (`12350`).
- **Tower chests vs reward chests:** `BlightTowerChestRank1..4` (`Chests 1772..1775`) have no `Transitionable` component and are spawned only by Atlas notable **Templar Ingenuity** (`map_blight_towers_are_chests`). Regular `"Blight"` reward lanes use `BlightMushrunes` / `MushrunesUber` / `MushrunesBlightRavaged` / `BlightInfectedMapsT1..T16` (`1750..1771`).
- **Blighted vs Blight-ravaged maps (`OnMapOpening`):**
  - Both disable the Atlas passive tree (`UsesAtlas = false`) and use `BlightTopologies` row 64 (`BlightedMapTree1`, 20 portals, 20 pump HP, 300 s base duration reduced by Teal Oil).
  - Blighted maps (`InfectedMap`, `HASH16 51426`): up to 3 oil anointments, 25% of map IIQ applied to lane chest counts.
  - Blight-ravaged maps (`UberInfectedMap__`, `HASH16 2167`): overrides area level to **85** (`map_item_level_override`), `+200%` more monster life, `+20%` monster movement speed, up to **9** oil anointments (**max 3 of the same oil**), 50% of map IIQ applied to lane chest counts, and exclusive **Stranglegasp** drops.
- **Anointing & Oil Extractor (`BlightAnointing.cs`, `Progression.cs`):**
  - Reads all 576 `BlightCraftingRecipes`: Type 0 Amulets & 7 `BlightCraftingUniques` (incl. 31 Prismatic Oil recipes & **Worship the Blightheart**, cap 2 on Cowls, cap 4 on Stranglegasp), Type 1 Rings (91 recipes), Type 2 Blighted/Blight-ravaged maps (13 recipes).
  - Corrupted items require a **Tainted Oil** (`UseType = 1`); Mirrored items require a **Reflective Oil** (`UseType = 2`, routed to Blight tab cell `78`).
  - `GrantedPassive`..`GrantedPassive5` (`mod_granted_passive_hash`..`_5`) are read per-item in `Progression.EquipmentStatSources` (memoized per immutable `Item` record; zero extra work on stat cache hits), skipped when already allocated on the passive tree, and contribute attributes during gear qualification.
  - **Oil Extractor** (`CurrencyExtractOil`, `Action = "extract_oil"`, `C→S 0x0071`) allows corrupted anointed targets, destroys the item, and returns one random oil from its anoint recipe.
- **Blight Uniques (`BlightSpores.cs`):** **Breathstealer** spawns 10 s weighted `BlightedSporeAuras` spores on rare kills; **Sporeguard** and **Worship the Blightheart** create Fungal Ground and trigger Sporeguard's 10% chaos corpse explosion.

## Open client-verification items

Pending `Client/Game.exe` RE and live capture (`scripts/dev/live.ps1` + `logs\`):

1. Exact `S→C 0x007b` `kind` byte for `BlightCraftingWindow` (screen table `0x142fd89f0`).
2. Exact field layout of Blight HUD packets `S→C 0x0266`–`0x0269` (dispatcher cases `0xc7`–`0xca`).
3. Live confirmation of `C→S 0x0264` (2 B Fast-Forward) and `C→S 0x026b` (6 B Anoint click) / `S→C 0x026c`–`0x026d`.
4. `BlightPortal` / `BlightPathway` `.ot` state names and `Beam` flags.
5. Live end-to-end verification of all 30 tower casts/buffs, Cassia dialogue, anointing, Blighted/Blight-ravaged maps, and `stat-builds` hot-path CPU measurement.
