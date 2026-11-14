# The Shaper

The Atlas pinnacle boss (`MonsterVarieties 5276` `Metadata/Monsters/AtlasBosses/TheShaperBoss`, "The Shaper's Realm",
area level 84), and the Shaper that stands beside the Elder in the Uber Elder arena (`5281`
`TheShaperBossUberElder`). This page records what the client data says about the fight, what the Gateway models
today, and what is still open. It is the reference for `Modules/Core/Game/Monster/ShaperFight.cs` and
`AtlasBosses.cs`.

All numbers below are read from the 3.29.3c client: the tables through `data/client.db` (one table per client
`.dat`), the metadata through the game's `Content.ggpk`. The extraction commands and the raw dumps are in
[the preparation bundle](#sources).

## Entry and the arena

`WorldAreas 1255` `MapWorldsShapersRealm` ("The Shaper's Realm"): area level 84, `IsMapArea` and `IsUniqueMapArea`,
`Bosses_MonsterVarietiesKeys` **[5276]**, `ModsKeys` [7490], `TopologiesKeys` [1574], `FirstEntry_NPCsKey`
`Metadata/NPC/Shaper` (the Shaper speaks on entry), `EndQuestFlags` [59]. The boss is placed by that area row, **not by a
pack**: `MonsterPacks` has no row for this area and no row names a Shaper variety in `BossMonster_MonsterVarietiesKeys`.
`WorldAreas 1297` `MapWorldsElderArenaUber` carries the same name and level and holds the Uber Elder pair
(`Bosses_MonsterVarietiesKeys` [6384, 5281]).

The arena's middle marker is `Metadata/MiscellaneousObjects/ArenaMiddle` (the Shaper's own
`Metadata/Monsters/AtlasBosses/TheShaper/ArenaMiddle` is the same object). Both are `server_only = true`: there is no
client object, only a cell the server reads — the Gateway's `ArenaBossFight.ArenaCentre` looks it up by that path.

The realm's terrain is `metadata/terrain/endgame/mapatlasshaper` (one `bossarenas/<boss>/rooms/boss.arm` per boss and
the gauntlet rooms). **No `.ais` file exists in the client archive** (0 of 1,245,702 paths), so the Shaper's AI is not
in the game data: his behaviour has to come from the tables and the art below.

## Forms and statistics

| row | Id | kit | life × | damage × | boss bar |
|---|---|---|---|---|---|
| 5276 | `TheShaperBoss` | 481, 6093, 6106, 6089, 6092, 6091, 6090, 6111, 3429, 6112, 6113, 6114 | 2560 | 200 | yes |
| 5277 | `TheShaperBossStandalone` | 481, 6093, 6106, 6091, 3429, 3571 | 2560 | 200 | no |
| 5279 | `TheShaperBossGauntlet` | the 5276 kit + 3684 | 2560 | 200 | yes |
| 5280 | `TheShaperBossElder` | — | 2560 | 200 | no |
| 5281 | `TheShaperBossUberElder` | 481, 6093, 6106, 6091, 6090, 6111, 6802, 6115 | 1648 | 200 | yes |
| 5283–5287 | the fragments (beam / projectiles / ambush / melee only) | one effect each | 2560 | 200 | no |
| 10307 | `UberMapShaperDaemon` | 6094, 6107, 6091 | 2560 | 200 | no |

At level 84, the way the Gateway computes it (`MonsterCatalog.BaseLife`, `MapBossScaling`, `Monsters.cs`):

- life = `DefaultMonsterStats` 16265 × 2560 / 100 = 416,384 → × 22.63 (map boss: `MonsterMapBossDifficulty` 2046 % +
  `MonsterMapDifficulty` 117 %) = 9,422,770 → × 7.98 (unique rarity `MonsterUnique2` +698 %) = **75,193,704**
- hit = `DefaultMonsterStats.Damage` 821.73 × 200 / 100 × 1.755 s = 2,884 ± 20 % → × 1.16 (map damage +16 %) =
  **2,676 – 4,015**
- the Uber row (5281) has life ×1648 → **48,405,911**
- `AtlasUberMonsterLifeScalingPerLevel` (level 84: `MoreLife` 275) exists but nothing in the server reads it.

His mods: `MonsterMapBoss` (4134), `CannotBeSlowedBelowValueBosses` (4686), `StunRepeatImmunityMapBossSuper` (23585,
no stun while stunned and none for 4 s after), `PinnacleAtlasBoss` (32441), `MonsterDroppedItemsInfluenceShaperBoss`
(33057) and, as a special mod, `ImmuneToKnockback` (7475). The fragments and the Uber Elder row carry
`MonsterNoDropsOrExperience` (6883) instead.

## The kit

`GrantedEffects` → `ActiveSkills` → `ActionTypes` → `GrantedEffectStatSets(PerLevel)`. The per-level rows are picked by
`PlayerLevelReq`, not by gem level: the level-84 fight reads the row with `PlayerLevelReq` 84, and the Uber tier is the
row with 85 (6112-6114 have both; 6120-6123 only the 85 row).

| effect | skill | action type | cast | what the data says |
|---|---|---|---|---|
| 481 `ShaperMelee` | `melee` | `Melee` | 1000 ms | the default attack: +33 % final attack speed, no damage stat of its own (the monster's default hit) |
| 6093 `AtlasBossCelestialBeam` | `shaper_beam` ("Shaper's Ire") | `ShaperBeam` | 1000 ms | `base_skill_effect_duration` 3000 ms; damage per minute: fire 323,067, cold 264,915, lightning 365,066 (**15,884/s**) |
| 6106 `AtlasBossAcceleratingProjectiles` | `shaper_projectile` ("Shaper's Despair") | `ShaperProjectile` | 1400 ms | the three orbs of `Metadata/Projectiles/ShaperProjectile` (row 220: speed 5, collision radius 4); cold **5,639 – 8,459** a hit, always pierce, 25 % cold penetration, 5 % spell crit |
| 6089 `AtlasBossRetreat` | `shaper_banish_zana` | `ShaperBanishZana` | 2000 ms | empty stat set: the blink away, no damage |
| 6091 `AtlasBossFlickerSlam` | `shaper_teleport_slam` ("Shaper's Devastation") | `ShaperTeleportSlam` | 2000 ms | `is_area_damage`, +200 % final skill damage, −50 % final attack speed; the hit is the monster's default hit |
| 6111 `AtlasBulletHell` | `shaper_bullet_hell` | `ShaperBulletHell` | 5000 ms | the ring of `ShaperBulletHellProjectile` (row 223: 375 units/s, radius 3): the same cold 5,639 – 8,459 a hit, `projectiles_nova`, `number_of_additional_projectiles` **11** (so twelve in the ring, as the server flies them), 25 % cold penetration |
| 6090 `AtlasBossPortalWarp` | `shaper_open_portal` | `ShaperOpenPortal` | 2000 ms | `base_skill_effect_duration` **25 000 ms** (the portal stands for 25 s) |
| 6092 `AtlasBossTeleportPlayer` | `shaper_banish_player` | `ShaperBanishPlayer` | 1000 ms | empty stat set: the mechanic has no numbers in the tables |
| 3429 `ShaperSummonFragment` | `summon_specific_monsters_on_self` | `SummonSpecificMonsters` | 1000 ms | 1 monster (2 at the 85 tier), radius +120 %, `summoned_monsters_are_minions`, `monster_no_drops_or_experience` |
| 3571 `SSMStandaloneShaperAnomaly` | `summon_specific_monsters_on_self` | `SummonSpecificMonsters` | 1000 ms | the standalone variant's summon |
| 3684 `SSMShaperGauntletMiniboss` | `summon_specific_monsters_on_target` | `SummonSpecificMonsters` | 1000 ms | the gauntlet's summon, on the target |
| 6112-6114 `AtlasBulletHellUberPhase1..3` | `shaper_bullet_hell` | `ShaperBulletHell` | 5000 ms | the three Uber bullet-hell phases |
| 6115 `AtlasBulletHellUberElder` | `shaper_bullet_hell` | `ShaperBulletHell` | 5000 ms | the Uber Elder's ring (authored at the 85 tier) |
| 6802 `TeleportShaperUber` | `teleport` | `Teleport` | 1170 ms | the Uber Shaper's blink (the rig's `teleport` clip is 1166 ms) |

The fragments (5283-5287, 10307) each carry one effect of their own: 6094 (beam), 6107 (projectiles), 6108/6109
(projectiles, ambush), 481 (melee).

The buffs that matter: `uber_shaper_immune_buff` (1028) and `uber_elder_immune_buff` (1027) — `base_cannot_be_damaged`,
`base_cannot_be_stunned`, `cannot_die`, `untargetable_by_monster_ai`, `immune_to_curses`,
`immune_to_auras_from_other_entities` — and `shaper_immune` (764). `shaper_beam` (765) is the buff the native beam puts
on the player (`base_fire/cold/lightning_damage_taken_per_minute`), the same numbers the effect's stat set carries.

## Animation: the pools and the clips

`Metadata/Monsters/AtlasBosses/TheShaper/TheShaper.act` maps the **action type** to the rig clip the client plays:

| action type | clip | | action type | clip |
|---|---|---|---|---|
| Melee | `attack1..attack4` (+ `basic_attacks.ao`) | | SpellProjectile | `banish` |
| Channel | `beam` | | SpellProjectileCold | `spellcast2` |
| SpellAreaOfEffect | `teleport` | | SpellBuff | `teleport` |
| **SpellAreaOfEffectCold** | **`beam_start`** (the Celestial Beam) | | Taunt | `taunt` |
| BarrageStart | `bullethell_start` (+ `bullet_hell_cast.ao`) | | GroundSlam | `teleportslam_appear` (+ `teleport_appear.ao`) |
| CastFast | `bullethell` | | Submerge | `teleportslam_disappear` (+ `teleport_disappear.ao`) |
| LongOpen | `portal_open` (the portal warp) | | ChangeToStance2 | `removehood` |
| Dance | `portal_hold` | | Teleport | `teleport` |
| Run / Idle / TakeHit / Block / Death | `run` / `idle` / `flinch` / `flinch` / `death` | | | |

Every clip has a `_nohood` twin, because the object type switches the actor to the **NoHood** stance at half health
(`ChangeToStance2 = "removehood"`). The rig (`Art/Models/Monsters/TheShaper/TheShaperBody/rig.amd`, 61 clips) gives the
lengths and the contact keyframes that decide when a cast lands:

| clip | length | contact | | clip | length | contact |
|---|---|---|---|---|---|---|
| attack1 / attack3 | 1500 | 466 | | **beam_start** | **2500** | **2500** (the beam lands at the clip's last frame) |
| attack2 | 1500 | 533 | | bullethell | 366 | 332 |
| attack4 | 1500 | 500 | | **bullethell_start** | **3000** | **2866** |
| banish | 1333 | 533 | | portal_open | 1500 | 833 |
| **spellcast2** | **2133** | **433, 966, 1533** (three release points) | | portal_hold | 533 | — (loop) |
| **teleportslam_appear** | **1500** | **800** | | removehood | 5000 | — |
| **teleportslam_disappear** | **1000** | **566** | | phase2_dialogue / _hood | 5000 | — |
| **teleport** | **1166** | **566** | | phase3_dialogue1 / 2 | 4000 / 3000 | — |
| flinch / flinch_long | 500 / 1000 | — | | death | 14000 | remove_collidability@150 |

`TheShaperUberElder.act` is the same except `Death = death_uber` (5033 ms, with `deathUber.ao`).
`TheShaperElderEncounter.act` (the Shaper beside the Elder) has only Melee, LongOpen, Dance, Taunt and the locomotion —
no beam, no bullet hell.

## The object type: the phases

`Metadata/Monsters/AtlasBosses/TheShaper/TheShaper.ot` extends `Metadata/Monsters/AtlasBosses/AtlasBosses` (which
extends `Metadata/Monsters/Monster` and `Metadata/Monsters/BossLifeBar`) and carries:

The server already knows the object type: `object_types` (client table) holds its component list —
`Positioned, Stats, ObjectMagicProperties, BaseEvents, Pathfinding, Buffs, Life, Animated, Inventories, Actor,
Monster, StateMachine, Transitionable, NPC, MinimapIcon, Render, Targetable, Timer` — and
`transitionable_num_states` **13**. So the fight can drive the phases with the message the protocol already has:
`InstanceEntities.TransitionableUpdate(bossId, 12, state)` (component index 12 is `Transitionable`), the call
`DepravedTrinity` already uses for its boss. The map-boss type is the only one with the machine: the standalone type
(`TheShaperStandalone`) merges to eleven components — **no `Transitionable`, no life bar** — and the Uber type
(`TheShaperUber`) has one state. The Shaper's own `.ot` adds:

- `Transitionable { num_states = 13 }` — 1 pre-battle, 2 talking, 3 fighting/banishing, 4 waiting, 5/6/7 talking
  (half health), 8/9 fighting/waiting, 10/11/12 talking (quarter health), 13 fighting;
- `on_transition_to_5`: `ShaperHalfHealthA` + `PlayAnimationByName(removehood)` — **half health drops the hood** (the
  actor changes stance, so every clip after it is a `_nohood` one); `on_transition_to_7`: `ShaperHalfHealthB` +
  `phase2_dialogue`; `on_transition_to_10`: `ShaperQuarterHealthA` + `phase3_dialogue1`; `on_transition_to_12`:
  `ShaperQuarterHealthB` + `phase3_dialogue2`; a `Timer` plays `phase2_dialogue_hood` 7 s after the intro;
- `Stats { set_immune_vaal_lighting_warp = 1 }`, `NPC { marker_enabled = false }`, `MinimapIcon { icon = "NPC" }`
  (hidden on every transition into a fighting state), and `on_construction_complete` adds the effect pack
  `…/TheShaper/possessed_boss/shaper_fx.epk`.

`TheShaperUber.ot` (the Uber Elder arena's Shaper) is a different object type: it extends `Monster` + `BossLifeBar`
directly, has a `StateMachine` block, adds `…/elder_puppet/TheShaper/elder_puppet.epk` on construction and — important
for the server — `Buffs { on_lose_uber_shaper_immune_buff = { SpawnEffect('…/immune_shield/immune_shield_off.ao'); } }`:
**the client plays the shield-off art itself when the immunity buff ends**, so a server that also sends its own
shield-off effect shows it twice.

The fragments are their own object types: `TheShaperFragment.ot` and `TheShaperFragmentUberMap.ot` extend
`AtlasBosses` and carry a **`DiesAfterTime`** component (the fragment expires), and
`TheShaperInfluenceAmbush.ot` is a projectile-like monster (`Positioned { collision_hull_type = "projectile",
blocking = false }`, `base_cannot_be_damaged`, `base_cannot_be_stunned`, `cannot_be_knocked_back`,
`untargetable_by_monster_ai`, `Actor { basic_action = "Emerge" }`, and on the client `targetable = false` with the
rendering disabled on every transition).

### What the client does by itself

- **The life bar** is a separate base type (`Metadata/Monsters/BossLifeBar`) with the shared states
  `boss_life_bar` / `disable_life_bar` / `global_first_aggro`; the server writes those and
  `MonsterVarieties.BossHealthBar` (1 on 5276, 5279, 5281) has to be set, or the static bar never appears.
- **The effect packs** are client-side: `shaper_fx.epk` on construction (and removed on death by the standalone type),
  `elder_puppet.epk` for the Uber variant, `immune_buff.epk`, `shieldup.epk`/`shielddown.epk`, `beam_buff.epk`,
  `minion_death.epk` and `space_ignite.epk`. The packs are UTF-16 scripts: `shieldup.epk` is
  `AttachedObject "<lock>" "…/immune_shield/immune_shield.ao"` (the shield hangs on the boss's **lock** bone),
  `shielddown.epk` the same with `immune_shield_off.ao`, and `immune_buff.epk` attaches `immune_buff.ao` **and**
  `banish_pulse.ao` to every bone (`"*"`).
- **Death is an entity, not a flag**: `TheShaperDeathEffect` (plays `death_ambushed`, 66,333 ms) or
  `TheShaperDeathEscapeEffect` (`death_escape`, 5,500 ms) has to be spawned at the corpse, and Zana needs a shared state
  `enable` for the effect to flip.
- **Two server-only markers** belong to the arena: the middle marker (`arenamiddle.ot`) and the bullet-hell anchor
  (`theshaperbullethell.ot`, `no_serialise = true`); neither is ever sent to a client.
- **The Elder-scene Shaper** has a `beam` shared state (0/1); when it is 1 the client attaches
  `ElderGlyphChannel/rig.ao` by itself.
- The `.act` also carries `movement_speed 337.5` — the actor's movement speed in the action set, which is **not** the
  variety's `MovementSpeed` (20).

## What the server models today, and what is still open

Modelled: the melee, the Celestial Beam, the accelerating projectiles, the flicker slam, the retreat (the `teleport`
clip and the `instant_teleport` art) and the bullet-hell ring (twelve projectiles in a circle) behind the immunity shield
at the 75/50/25 % thresholds. Each of them is played as the skill's own action (0x1ab), so the client animates the boss
and draws the skill's art; the server adds what only it knows — where the hit lands and how much it takes. The rig's
contact keyframes say when and where that is:

- **the Celestial Beam**: the ground it burns is a **wide straight band**, not a line and not a flare (checked
  against the original by the player): the effect's art covers a strip roughly ten cells wide, and walking out of that
  width is how you live. The band runs from the Shaper to the point he aimed at and is pinned there (the beam does
  not follow the player). The burn starts with the cast and runs for the skill's own three seconds, because the
  ground under the effect (the effect rig's `zone_*` animations) is part of the effect. The data gives the beam no
  width, so the five-cell half-width is our figure.
- **the Celestial Beam**: the cast is the rig's `beam_start` (2500 ms, its contact the last frame), so the charge is
  2.5 s and the beam lands at its end. The burn is the skill's own three seconds
  (`base_skill_effect_duration` 3000 ms) and its damage is the fire/cold/lightning degen of the stat set
  (15 884/s). The burn is a **line** from the Shaper to the point he aimed at (the width is a development figure, the
  data gives none), and the player can walk out of it. The client draws the beam itself, and it stretches the effect's
  animations over the action's time: the effect's rig
  (`Art/Models/Effects/monster_effects/AtlasofWorldsBoss/TheShaper/beam/rig.amd`) is `beam_start_01` (500 ms, the orb
  growing) then the looping `beam_idle_01` (the ray) and `beam_end_01` (250 ms), so the action has to run the whole beam
  — the charge and the three seconds it burns — or the ray is over before the ground stops burning. The server must
  also send the skill's own numbers in the boss's skill records, or the client ends the effect with the cast (the same
  finding as the player's Spark: a skill whose duration the Gateway does not send dies on its first tick,
  [11](../../docs/protocol/11-skills.md)).
- **the accelerating projectiles**: three orbs, one per contact of the rig's `spellcast2` (433, 966 and 1533 ms of its
  2133 ms), each from the hand that throws it (the contacts' own positions in the rig). The client's tables hold only a
  pool's first contact, so the other two come from the rig.
- **the flicker slam**: the boss vanishes and reappears beside the player; the impact lands on the clip's contact,
  800 ms into `teleportslam_appear`, where the Shaper stands (the rig's contact is at his own origin), for the monster's
  default hit with the skill's +200 % final skill damage (three times the base hit), in a 9 cell area.
- **the bullet hell**: a nova of twelve around him, released on the rig's `bullethell` contact (332 ms of its 366 ms)
  from his own centre, with the skill's own cold damage.

Open, with what the data adds for each:

0. **The phase machine is not driven at all.** The server today only uses life thresholds for the shield
   intermissions; the client's thirteen states (the intro at 2, the half-health `removehood` at 5, the phase-2
   dialogue at 7, the phase-3 dialogues at 10 and 12, the final state 13) are never set, so the Shaper never speaks and
   never takes his hood off. The data says what each state obliges the server to do: enter 2 when the player arrives
   (the client plays the intro line and starts a 7 s timer that fires `phase2_dialogue_hood`), leave it no earlier than
   7 s later, and step into 5 at half health (the `removehood` clip, rig index 46), 7 (the phase-2 dialogue, index 38),
   8 (phase-2 combat), 10/12 (the quarter-health dialogues, indices 40 and 41) and 13 (the final phase). Re-entering a
   state replays its line, so the server must not oscillate across a threshold. The client walks the intermediate
   states one at a time and runs each one's event, so the server must step one state per message and never jump
   (1 → 7 would skip `removehood`); the entity is created in state 1, never 0.
1. **The fragment summon (3429)** — the skill summons **one** monster within a +120 % radius, as a minion, with no drops
   and no experience. The variety is not named in any table (stat 885 `alternate_minion` carries 194, which is
   `GhostPirateGreenWandMaps` "Phantom Occultist"), so the only candidates are the fragment rows 5283-5287/10307, whose
   object types carry `DiesAfterTime`. Which of them the summon picks is not in the data, and the `DiesAfterTime`
   block of `TheShaperFragment.ot` is empty, so the fragment's lifetime is not in the data either (the summon does say
   `summoned_monsters_are_minions`, and the stat `minion_dies_when_parent_dies` exists for that).
2. **The teleport of the player (6092)** — `ShaperBanishPlayer`, 1000 ms, no numbers: the mechanic is the banish
   itself (the rig's `teleport` clip, `banish` art).
3. **The portal warp (6090)** — `LongOpen` (`portal_open`, 1500 ms, contact 833) opens it, `Dance` (`portal_hold`,
   533 ms loop) keeps it, and the effect's own stat says the portal stands for **25 s**.
4. **The Uber phases (6112-6114, 6115, 6802)** — the same bullet hell at the 85 tier and the Uber teleport; the Uber
   Elder arena's Shaper does not have the fragment summon, the retreat or the player teleport.

Differences between the model's timings and the client's own, worth checking when the fight is fixed:

- the bullet-hell cast clip is 3000 ms with the contact at 2866 ms, while the model fires its three novas 1.4 s apart;
- the Shaper's own clip list has a `_nohood` twin for every clip, so a server that plays clips by index has to switch
  to the twins once the hood is off at half health;
- the skill records the client reads the duration from are the boss's own: `AtlasBosses.SkillsFor` sends each effect's
  stats (`SkillCatalog.EffectStats`) as well as the swing's, so the client knows how long an effect stands;
- after the half-health transition the actor is in the NoHood stance, so a server that plays clips by index has to use
  the `_nohood` indices (18 → 19 for `beam_start`);
- the Uber Shaper's own object type plays the shield-off art when the immunity buff ends.

## Sources

- tables: `data/client.db` (`MonsterVarieties`, `WorldAreas`, `GrantedEffects`, `ActiveSkills`, `ActionTypes`,
  `GrantedEffectStatSets(PerLevel)`, `Stats`, `Mods`, `BuffDefinitions`, `Projectiles`, `MonsterTypes`,
  `DefaultMonsterStats`, `MonsterMapBossDifficulty`, `MonsterMapDifficulty`, `MultiPartAchievements`,
  `object_types`);
- metadata and rig: `Client/Content.ggpk` via `scripts/re/ggdump` — the Shaper's `.ot`, `.ao`, `.act`, `.epk` and
  `Art/Models/Monsters/TheShaper/TheShaperBody/rig.amd`;
- the full dumps (kit tables, animation pools, the archive inventory, every Shaper path in the archive) are in the
  preparation bundle under `artifacts/shaper/` (`kit.md`, `assets.md`, `anim-rig.md`, `archive.md`,
  `shaper-paths.txt`);
- re-extracting from the archive (the prebuilt dumper; `dotnet run` cannot restore packages on this machine):

  ```bash
  # the whole index (1,245,702 paths)
  ./scripts/re/ggdump/bin/Debug/net10.0/GgDump.exe "Client/Content.ggpk" artifacts/shaper plan
  # the Shaper's metadata, effect packs and animation pools
  ./scripts/re/ggdump/bin/Debug/net10.0/GgDump.exe "Client/Content.ggpk" artifacts/shaper/ggpk extract \
    "^metadata/(monsters/atlasbosses/theshaper|effects/spells/monsters_effects/atlasofworldsbosses/theshaper)" \
    "\.(dds|png|bmp|ogg|tga)$" 1
  # the rig (the clip lengths and contact keyframes)
  ./scripts/re/ggdump/bin/Debug/net10.0/GgDump.exe "Client/Content.ggpk" artifacts/shaper/ggpk extract \
    "^art/models/monsters/theshaper/" "\.(smd|sm|dds|ast)$" 1
  ```

  The extracted tree belongs in the repo's GGPK cache (`Tools/re/out/ot/`, path "/" → "_"), where
  `scripts/re/ot_components.py` (`read_text`, `amd_animations`) reads it.
- the code: `Modules/Core/Game/Monster/ShaperFight.cs`, `AtlasBossFight.cs`, `AtlasBosses.cs`,
  `src/GameServer.Gateway/World/MapBossFights.cs`; the live harness is `scripts/dev/boss_test.py --boss shaper`.
