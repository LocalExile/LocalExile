# The Pantheon

How the Pantheon works in the 3.29 client and what the server must do for the campaign: which kills unlock which god,
how the player chooses a major and a minor god, how the choice is shown, and what each god grants. Part of the
[campaign quest reference](../campaign-quests.md).

Sources: `data/tables` (PantheonPanelLayout, PantheonSouls, NPCTalk, QuestFlags, ClientStrings, Stats), RePoE 3.29.3.3
stat translations, the wiki (Fandom mirror, Pantheon page, 2026-10-30), and the client (`Client/Game.exe`,
`scripts/re/xr.py`, `dis.py`, the C→S list in [c2s.md](../../protocol/c2s.md)). Played live once, before the game moved
into modules (§7); the panel's display of the choice is not checked yet.

## 1. The tables

**PantheonPanelLayout** (16 rows): one row per panel slot. `IsMajorGod`, `QuestFlag` (the flag that unlocks the god),
`GodName1` … `GodName4` with `Effect1` … `Effect4` (stats and values: effect 1 is the god's own power, effects 2–4 the
upgrades), `IsDisabled`. Rows 4, 5, 7 and 12 (`Minor God 1`, `2`, `4`, `9`) are disabled placeholders with
`QuestFlag` `NEVER_SET`. So there are 4 major and 8 minor gods.

**PantheonSouls** (20 rows): the upgrades. Each row is a map (`WorldArea`), its boss (`CapturedMonster`), the captured
soul item (`CapturedVessel`, `Metadata/Items/PantheonSouls/PantheonSoul<God>Upgrade<N>`), the panel row
(`PanelLayout`) and the flag the upgrade sets (`QuestFlagUpgrade`, e.g. `BrineKingUpgrade1`) or clears on downgrade.
Upgrades come from Divine Vessels in maps (endgame) and are handed to Sin ("Unlock Power", script
`UnlockPantheonPowerFromItem()`); ClientStrings 7215 `PantheonUpgradeUnavailableTillEndgame`: "Upgrade effects do not
apply until the quest An End to Hunger is completed". Not part of the campaign automation.

The decoded `LeagueQuestFlag1–3` of PantheonPanelLayout and `__col6` of PantheonSouls give unrelated names
(`WeylamOnMerveil`, `Visited-8_2` …) when read as QuestFlags rows: they point at another table (probably the client's
built-in league quest flag list, [21](../../protocol/21-hideouts.md) §1). Not needed.

## 2. Unlocking gods in the campaign

A god is unlocked when the character has the row's `QuestFlag`: the client reads it from the quest flags it already
receives (S→C 0x2ba Player). Every unlock flag is a boss kill the act's quest engine sets anyway:

| Panel row | God | Major | Unlock flag | Quest | Boss kill (MonsterVarieties Id under `Metadata/Monsters/`, area): placement in the act file |
|---|---|---|---|---|---|
| 0 | The Brine King (`TheBrineKing`) | yes | `a6q1-KilledBoss` | a6q1 The Brine King ([act 6](act6.md)) | Tsoagoth, The Brine King (`Metadata/Monsters/BrineKing/BrineKing`), The Brine King's Reef 2_6_15 |
| 1 | Arakaali | yes | `a7q4-KilledBoss` | a7q4 The Mother of Spiders ([act 7](act7.md)) | Arakaali, Spinner of Shadows (`VaalSpiderGod/Arakaali`), The Temple of Decay Level 2 2_7_12_2 |
| 2 | Solaris | yes | `a8q23-KilledBosses` | a8q2/a8q3 Lunar and Solar Eclipse ([act 8](act8.md)) | Solaris, Eternal Sun and Lunaris, Eternal Moon (`LunarisSolaris/Solaris`, `/Lunaris`), The Harbour Bridge 2_8_13 |
| 3 | Lunaris | yes | `a8q23-KilledBosses` | same | same |
| 6 | Abberath | no | `a6q7-KilledBoss` | a6q7 The Cloven One ([act 6](act6.md)) | Abberath, the Cloven One (`Abberath/Abberath`, `Abberath2`), Prisoner's Gate 2_6_8 |
| 8 | Gruthkul | no | `a7q9-KilledBoss` | a7q9 Queen of Despair ([act 7](act7.md)) | Gruthkul, Mother of Despair (`DespairBear/DespairBear`), The Dread Thicket 2_7_9 |
| 9 | Yugul | no | `a8q4-KilledBoss` | a8q4 Reflection of Terror ([act 8](act8.md)) | Yugul, Reflection of Terror (`Frog/FrogGod/SilverFrog`), The High Gardens 2_8_4 |
| 10 | Shakari | no | `a9q5-KilledBoss` | a9q5 Queen of the Sands ([act 9](act9.md)) | Shakari, Queen of the Sands (`Shakari/Shakari`, `Shakari2`), The Oasis 2_9_4 |
| 11 | Tukohama | no | `a6q3-KilledBoss` | a6q3 The Father of War ([act 6](act6.md)) | Tukohama, Karui God of War (`Tukohama/Tukohama`), The Karui Fortress 2_6_5 |
| 13 | Ralakesh | no | `a7q1-KilledBoss` | a7q1 The Master of a Million Faces ([act 7](act7.md)) | Greust, Lord of the Forest (`Ralakesh/Greust/Greust`, Ralakesh's avatar; in WorldAreas `Monsters`, not `Bosses`, so the server does not place him today), The Ashen Fields 2_7_7 |
| 14 | Garukhan | no | `a9q2-KilledBoss` | a9q2 The Ruler of Highgate ([act 9](act9.md)) | Garukhan, Queen of the Winds (`Garukhan/Garukhan`), The Quarry 2_9_8 |
| 15 | Ryslatha | no | `a6q6-KilledBoss` | a6q6 The Puppet Mistress ([act 6](act6.md)) | Ryslatha, the Puppet Mistress (`InsectSpawner/InsectSpawnerParasiteBoss`), The Wetlands 2_6_11 |

The bosses' areas, varieties and whether the server places them are in the act files.

**Sin.** In the real game Sin appears beside each defeated god (`Metadata/NPC/Act6/SinTukohama`, `SinAbberath`,
`SinRyslatha`, `SinBrineKing`, `Act7/SinRalakesh`, `SinGruthkul`, `SinArakaali`, `Act8/SinYugul`,
`SinSolarisLunaris`, `Act9/SinGarukhan`, `SinShakari`). None of them is a room or tile line of the generated layouts
(`lg.ps1 questobjects`), so the real server spawns them after the kill. Their option 0 (e.g. NPCTalk 993 "Tukohama",
needs `a6q3-KilledBoss`) sets `Sin<God>Reward` (QuestFlag4 and 5) and runs
`SetSessionQuestFlag(CanChangePantheon); FinishNPCChat(); SendOpenScreen(PantheonScreen)`: the session flag
`CanChangePantheon` (QuestFlags 1409) lets the player choose a god right there, outside a town.

The town Sins (`Act6/SinTown` in Lioneye's Watch, `Act7`, `Act8`, `Act9`, `Act10/SinTown`, `Epilogue/Sin`; placed by
`TownNpcs` from the town rooms) offer the same conversation when the arena Sin was missed: e.g. NPCTalk 962 "Tukohama"
(needs `a6q3-KilledBoss`, not `SinTukohamaReward`, QuestFlag4 `SinTukohamaReward`), 971 "The Brine King", 1101
"Ralakesh", 1104 "Gruthkul", 1197 "Yugul", 1202 "Solaris and Lunaris", 1344 "Garukhan", 1348 "Shakari". These rows
have no script: the generic NPCTalk flags handle them. Nothing else reads `Sin<God>Reward` (no QuestStates row, no
area gate); only the cumulative presets carry them (`E3StartOfAct7`, `E3EndOfAct8`). So the campaign automation does
not need the arena Sins: the unlock flags come from the kills, and the choice is made in town.

## 3. Opening the panel

- The Pantheon button and its key (`ActionOpenPantheonPanel`, ClientStrings 2597; UI element `pantheon_button`) open
  the panel locally. The panel shows the unlocked gods from the quest flags.
- The town Sins' "Open Pantheon" / "View Pantheon" rows (NPCTalk 972, 1004, 1109, 1203, 1349, 1471, 1512 …, category
  38 = 0x26, need `a6q3-KilledBoss`, not `HardMode`) are handled by the client itself (it sends no 0x82 for category
  0x26, [17](../../protocol/17-npcs.md) §"C→S 0x82").
- The server can open it: S→C 0x7b kind **0x14** (`PantheonScreen` in the client's screen table at `0x142fc5920`), what
  the Sin scripts' `SendOpenScreen(PantheonScreen)` does.

## 4. Choosing a god: C→S 0x1ee

| Field | Value |
|---|---|
| Message | instance C→S **0x1ee**, 3 bytes: u16 id, **u8 = PantheonPanelLayout row + 1** |
| Sender | `0x14097dfc0` (the panel's select handler); class vtable `0x14345ef10`, writer `0x140e29080` (the shared u8 writer also used by 0x1a) |
| Encoding | the byte is `(selected row pointer − table base) / 286 + 1` (row size 0x11e), computed at `0x14097e21a` … `0x14097e241` |
| One message for both | the row's `IsMajorGod` tells whether it replaces the major or the minor god |

**Client-side gate** (before the send, `0x14097e000` … `0x14097e08d`): the client sends only when the current area is a
town or a hideout (area data `+0x14`, `+0x14e`), the Rogue Harbour (HASH16 `0xb5be` HeistHub, `0x3064`
HeistHubEndless), or when a session condition holds (`+0xd7`, and a lookup of hash `0xb1362a5d`; most likely
`CanChangePantheon`). Otherwise it shows ClientStrings row 2925 `PantheonLocationRestrictionWarning` ("You cannot change
your selected Pantheon Powers at this location …"; looked up with the 1-based id 0xb6e) and sends nothing.

**What the server does** with 0x1ee (the Pantheon module, `Modules/Core/Player/Pantheon`; Gateway `PantheonCatalog`):
1. Look up the row (byte − 1); refuse a disabled row or one whose `QuestFlag` the character lacks.
2. Check the location: town, hideout, or `CanChangePantheon` set in this session.
3. Store the choice per character (major and minor separately, by row `Id`, e.g. `TheBrineKing`, so it survives
   table changes), in the save.
4. Send the Player update below, and apply the god's stats (effect 1, plus effects 2–4 whose PantheonSouls upgrade
   flag the character has, only once `EndGameAvailable` is earned: every character is given it at login for the Atlas,
   so having it is not enough (`ClientState.EarnedQuestFlag`, `PantheonCatalog.ChosenStats`)).

## 5. Showing the choice: Player component bytes +0x1ae / +0x1ad

The Player component stores the selection in two bytes (debug dump `0x141e80603` … `0x141e80661`: "Minor Pantheon: "
`+0x1ad`, "Major Pantheon: " `+0x1ae`):

- **Creation blob** ([04](../../protocol/04-player-entity.md) §4.6): the five u8 after the WashedUp byte are `+0x1ae`
  (major), `+0x1ad` (minor), `+0x1b0`, `+0x1af`, `+0x238`. `PlayerObject` writes the saved gods into the first two (`PantheonMajor`,
  `PantheonMinor`: the row + 1, 0 for none) and zeros into the rest.
- **Update** S→C 0x2ba to the Player component (index 6): u16 flag word **0x0080**, then u8 → `+0x1ae` (major), u8 →
  `+0x1ad` (minor) (reader `0x141e7ee30`, the bit test at `0x141e7f22c`); the reader then calls the component's vtable
  +0x138 (refresh).
- The panel reads both bytes at `0x14097c83f` and passes them to a virtual (major, minor). The value is very likely the
  same 1-based row as 0x1ee (0 = none): **verify live** (send 0x0080 with 1, 12 → The Brine King and Tukohama
  highlighted).

## 6. What each god grants

Stats from PantheonPanelLayout (Stats Ids in the table; text through RePoE stat translations).

| Row | God (power) | Effect 1 (the god's power) | Upgrades (soul, map, upgrade flag: text) |
|---|---|---|---|
| 0 | Soul of the Brine King | `cannot_be_stunned_if_have_been_stunned_or_blocked_stunning_hit_in_past_2_seconds` 1: cannot be Stunned if Stunned or Blocked a Stunning Hit in the past 2 s | Nassar (Reef, `BrineKingUpgrade1`): `base_stun_recovery_+%` 30; Captain Tanner Lightfoot (Coral Ruins, `BrineKingUpgrade2`): `base_avoid_freeze_%` 100; Shock and Horror (Mineral Pools, `BrineKingUpgrade3`): `chill_effectiveness_on_self_+%` −50 |
| 1 | Soul of Arakaali | `degen_effect_+%` −10: 10% reduced damage taken from damage over time | Maligaro (Overgrown Shrine, `ArakaaliUpgrade1`): `life_and_energy_shield_recovery_rate_+%_if_stopped_taking_damage_over_time_recently` 20; Armala (Sunken City, `ArakaaliUpgrade2`): `debuff_time_passed_+%` 20; Drought-Maddened Rhoa (Arid Lake, `ArakaaliUpgrade3`): `additional_chaos_resistance_against_damage_over_time_%` 40 |
| 2 | Soul of Solaris | `physical_damage_reduction_%_if_only_one_enemy_nearby` 6, `take_half_area_damage_from_hit_%_chance` 20 | Oak (Mesa, `SolarisUpgrade1`): `elemental_damage_taken_+%_if_not_hit_recently` −8; Kitava (Lava Lake, `SolarisUpgrade2`): `self_take_no_extra_damage_from_critical_strikes_if_have_been_crit_recently` 1; Forest of Flames (Volcano, `SolarisUpgrade3`): `avoid_ailments_%_from_crit` 50 |
| 3 | Soul of Lunaris | `physical_damage_reduction_%_per_nearby_enemy` 1, `movement_speed_+%_per_nearby_enemy` 1 (each up to 8%) | Captain Clayborne (Frozen Cabins, `LunarisUpgrade1`): `base_avoid_projectiles_%_chance` 10; Fragment of Winter (Waterways, `LunarisUpgrade2`): `elemental_damage_taken_+%_if_been_hit_recently` −6; Glace (Beach, `LunarisUpgrade3`): `avoid_chained_projectile_%_chance` 100 |
| 6 | Soul of Abberath | `pantheon_abberath_ignite_duration_on_self_+%_final` −60 | Mephod (Summit, `AbberathUpgrade1`): `unaffected_by_burning_ground` 1, `movement_speed_+%_while_on_burning_ground` 10 |
| 8 | Soul of Gruthkul | `physical_damage_reduction_%_per_hit_you_have_taken_recently` 1 (up to 5%) | Erebix (Cemetery, `GruthkulUpgrade1`): `enemies_that_hit_you_with_attack_recently_attack_speed_+%` −8 |
| 9 | Soul of Yugul | `%_of_your_and_your_minions_damage_cannot_be_reflected` 50, `reflect_hexes_chance_%` 50 | Varhesh (Terrace, `YugulUpgrade1`): `curse_effect_on_self_+%` −30 |
| 10 | Soul of Shakari | `pantheon_shakari_self_poison_duration_+%_final` −50, `cannot_be_poisoned_if_x_poisons_on_you` 3 | Terror of the Infinite Drifts (Desert Spring, `ShakariUpgrade1`): `chaos_damage_taken_+%` −5, `chaos_damage_taken_over_time_+%_while_in_caustic_cloud` −25 |
| 11 | Soul of Tukohama | `while_stationary_gain_additional_physical_damage_reduction_%` 3 (per second stationary, up to 9%) | Tahsin (Siege, `TukohamaUpgrade1`): `life_regeneration_rate_per_minute_%_while_stationary` 120 (2% per second) |
| 13 | Soul of Ralakesh | `physical_damage_over_time_taken_+%_while_moving` −25, `no_extra_bleed_damage_while_moving` 1 | Drek (Fields, `RalakeshUpgrade1`): `cannot_gain_corrupted_blood_while_you_have_at_least_5_stacks` 1 |
| 14 | Soul of Garukhan | `shocked_effect_on_self_+%` −60 | The Blacksmith (Dunes, `GarukhanUpgrade1`): `cannot_be_blinded` 1, `avoid_maim_%_chance` 100 |
| 15 | Soul of Ryslatha | `life_flasks_gain_X_charges_every_3_seconds_if_you_have_not_used_a_life_flask_recently` 3, `life_recovery_+%_from_flasks_while_on_low_life` 60 | Gorulis (Infested Valley, `RyslathaUpgrade1`): `enemy_life_regeneration_rate_+%_for_4_seconds_on_hit` −50 |

Most of these are conditional defensive stats the server's damage model does not evaluate yet; for the campaign it is
enough to store and show the choice. Applying them belongs with [damage](../../protocol/20-damage.md).

## 7. What is built

**Implemented** (played live on 2026-10-30 before the game moved into modules: the campaign runner chose The Brine King
and Tukohama through 0x1ee and `/state` showed them; the panel's highlight of the choice is still to be checked):

- **Framework** (Gateway, Core): C→S 0x1ee framed (`InstancePantheon`, 3 bytes; unframed, the Gateway logged it as raw
  bytes and nothing was chosen), the Player update 0x0080 (`InstanceEntities.PlayerPantheonUpdate`) and the creation
  blob's two bytes (`PlayerObject.PantheonMajor/Minor`, written from the save), the choice per character
  (`CharacterSaves.SetPantheon`, `PantheonMajor` / `PantheonMinor` by row Id, `ClientState`), the tables
  (`Player/PantheonCatalog.cs`: rows, unlock flags, powers, the souls' upgrade flags).
- **The Pantheon module** (`Modules/Core/Player/Pantheon`, listed by every league that lists the quest tables): 0x1ee
  as §4 (refused for a disabled or locked row, or outside a town, hideout or the Rogue Harbour without
  `CanChangePantheon`; the client hears the gods back either way); the god's Effect1 stats, plus Effect2–4 whose
  PantheonSouls upgrade flag the character has once `EndGameAvailable` is earned (not merely given at login:
  `ClientState.UnearnedQuestFlags`, as Kitava's Act 10 penalty), join the character's stat sources
  (`GameEvents.PlayerStatSources`; most are conditional stats the damage model does not read yet); `/pantheon [<god> …]`
  (dev) and /state's `pantheon` field (`GameEvents.StateBuilding`) for the driver's `pantheon` verb and the bridge's
  `pantheon:select <row>`.
- **From the quests**: the unlock flags are the act tables' kills; the arena Sins appear on the kills (`On.Appears`),
  and their god rows set `CanChangePantheon` and open the panel (`SendOpenScreen(PantheonScreen)`, 0x7b kind 0x14:
  the Game module's NPCTalk scripts).

**Not built:** the souls (Divine Vessels in maps, Sin's "Unlock Power" and `UnlockPantheonPowerFromItem()`), so the
upgrades come only from flags set by hand; most powers' conditional stats are not evaluated by the damage model. The
panel itself (the Pantheon button and its key, `ActionOpenPantheonPanel`) is the client's own: it opens without the
server and lists the gods from the quest flags.
