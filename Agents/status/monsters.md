# Status: Monsters

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/monsters.md](../roadmap/monsters.md).

## Works (verified with the real client)

- Monsters belong to the area instance: re-entering it finds the living ones where they were, with their damage, and
  the dead stay dead; a new instance gets fresh packs.
  Code: `InstanceStore.cs`, `PopulateArea`, Gateway.
  Docs: [14](../docs/protocol/14-monster-packs.md) §2.
- Monsters: `/spawn_mob <row, Id or name>[@level] [friendly] [rarity] [name]`, `/mobs`, `/despawn`, `/mob_walk`,
  `/ai`: any of 10 345 of the 10 691 MonsterVarieties at a level and rarity, with full life for its level, hostile
  (red) or friendly, that chases the player when near (its attack aimed at the player, 0x1ab, client pathing).
  Code: `MonsterObject`, `MonsterCatalog.cs`, Gateway.
  Docs: [10](../docs/protocol/10-chat-and-commands.md) §5.
- Monster packs in areas: on entry the area fills with its MonsterPacks at the area level (The Coast: crab and Drowned
  packs, some magic, at least 2 with a rare; cannibal packs at the bonfires) and the unique boss at the camp's
  `mapboss` point with its boss pack (Fire Fury beside the campfire, verified live 2026-10-18);
  Seeded pack plans repeat across server restarts.
  room points are found by fitting rooms to the doodads `-Walkmaps` captures.
  The Tidal Island's boat rooms are too sparse to fit and use their captured placement: Hailrake stands on the wreck.
  Rooms with a boss point take their actual placement (captured or generated); an unplaced sibling room adds none.
  `mapboss<n>[WG]` is the n-th of the area's bosses, sub-graph rooms included (Brutus, Merveil).
  `mapboss`, `force:mapboss` (Kitava) and `mapbosspack` take every boss left, once (The Den's two, both seals).
  Named packs bring their boss (Pikerivet, The Burning Man); The Quay's Tolman stands at his spawner.
  The Ashen Fields' boss is the bandit lord the character spared in Act 2, or Greust (`MapBossOverride`).
  A boss whose point is off the walk map stands on the nearest walkable cell; the boss pack stands once per area.
  `CampaignBossPlacementTests` (`Game_BOSS_CHECK=1`) checks every boss of the act 1–10 areas the Gateway generates.
  Code: `MonsterPacks.cs`, Gateway (`PopulateArea`), `scripts/walkmaps`, `scripts/re/atlas room-points`.
  Docs: [14](../docs/protocol/14-monster-packs.md).
- Unique map bosses get the map tables' life and damage increases: `MonsterMapBossDifficulty` plus
  `MonsterMapDifficulty`, added as percentages (level 83: ×20.73 life, ×1.15 damage). The King in the Mists' humanoid
  form therefore has 45,614,731 life at level 83, matching poedb's display.
  Code: `MonsterCatalog.MapBossScaling`, `SpawnMonster`.
  Docs: [king-in-the-mists](../docs/game/king-in-the-mists.md).
- Emerge monsters (Gravel Eaters, Dripping Dead, the Twilight Strand's Drowned …) are created buried, cannot be hit
  while buried, and rise when the player comes within 24 cells: the client plays their emerge clip (Emerge action
  0x0101, then Transitionable 2), and they fight, die and give experience like other monsters.
  The ones the client shows targetable while they wait (Tide Striders, Blood Elementals …) can be hit and rise when
  hit; unique bosses keep their own entrance state.
  Their packs are placed again (The Coast's `Act1-SandspitterEmerge`, the Mud Flats' `Act1-ZombieMiredEmerge`);
  an instance keeps buried ones buried and risen ones risen.
  Verified live 2026-10-25 (`/spawn_mob` Gravel Eater, packs on The Coast, the Mud Flats, the Twilight Strand, and
  lurking Tide Striders on Tidal Island).
  Code: `MonsterEmerge.cs`, `Program.Emerge.cs`, Gateway.
  Docs: [10](../docs/protocol/10-chat-and-commands.md) §5.
- Monster hit reaction: a hit that leaves a monster alive and passes the wiki's stun roll
  requests native Actor TakeHit recovery (S→C 0x1d1, animation type 5).
  Stun immunity, a boss's 4 s immunity, an absent TakeHit profile or an existing recovery prevents it.
  A killing blow gets no flinch.
  [Chance and duration](../docs/game/juggernaut.md#stun) follow the wiki and `Character.ot`, not a clip's length.
  The actor's native ACT selects and fits the presented clip; the server does not force a rig index.
  Recovery stops server and client movement, holds AI and invalidates old ordinary swing echoes and contacts.
  Historical LocalExile moving-Drowned samples held one cell through recovery, then resumed a fresh AI walk.
  This standalone port has fresh automated coverage; native integration remains pending in the
  [port record](../docs/dev/ranged-monster-actions-port.md).
  Rig-only 0x1b2 did not stop locomotion; it is no longer used as the enemy's recovery packet.
  The weapon impact sound and the target's blood come from S→C 0x1af "action hit", sent on melee and attack-gem hits
  and on monster hits on the player (2026-10-25; not yet checked live after the timing cap).
  GGG's server sends it too, but with the monster action's own sequence and skill index 2, where we send 0xffff and 0
  (Sand Spitters, live capture 2026-10-30; [port record](../docs/dev/ranged-monster-actions-port.md) "Live reference").
  A level 1 Sand Spitter hits for 5–7 here and a constant 4 on live (5 on a crit); the cause is not known.
  Code: `HitReaction.cs`, `MonsterCatalog.TakeHit`, Gateway (`SendMonsterFlinch`), `scripts/re/monster_takehit.py` →
  `data/monster-takehit.json`.
  Docs: [11](../docs/protocol/11-skills.md).
- Magic and rare monsters roll their mods from Mods.dat (monster domain, spawn weights by the monster's tags, level,
  one per family): magic one affix shared by the monsters of one name in an instance, rare two to four (poewiki
  "Rarity"), with a rare name from the Words.dat monster lists.
  Magic monsters glow blue and rare ones gold: the server puts the hidden champion buff on them
  (`MonsterRarityBuffs`, BuffDefinitions 66 / 67; [14](../docs/protocol/14-monster-packs.md) §2).
  The client shows them on hover (verified live 2026-10-25: "Gorewalker" / "Extra Life", "Quick Hungry Corpse" /
  "Hasted").
  Their life, hit, attack time, walk (sent as movement_velocity_+%, the client walks at the same speed), resistances,
  physical reduction, block, evasion, critical strikes and regeneration act on the server (each checked live); the
  instance keeps them.
  Only the 37 mods the server can honour roll; the other 74 are listed in [14](../docs/protocol/14-monster-packs.md)
  §5.
  Code: `MonsterMods.cs`, `Program.MonsterMods.cs`, `MagicProperties.cs`.
  Docs: [14](../docs/protocol/14-monster-packs.md) §5.
- Monster decisions use 50 ms of elapsed game time, with no catch-up burst after pauses or slow steps.
  A melee chase repeats every 200 ms ([07](../docs/protocol/07-latency-visibility-movement.md) §5), in legs of 12 cells
  along its walk-up while the player is farther than a leg past its reach (the attack aimed at the player from within
  that), and a Move to a surround slot is cut to its first 12 cells the same way: each order's search then fits the
  client's one path budget a frame. A monster out of the wake zone (171 cells) stands on the server as it does on the
  client, with no order until it is back ([07](../docs/protocol/07-latency-visibility-movement.md) §5). A Move chase has a
  separate 100 ms budget and requires at least three cells of Manhattan target change, except that a walk still under
  way is ordered again every 200 ms to the same cell (a lockstep client's monster that a body stopped gets going again);
  on a lockstep client a walking monster's re-order waits for a frame start right after its estimate's step, at most
  two steps past the cadence ([07](../docs/protocol/07-latency-visibility-movement.md) §6).
  A ranged chaser stands off one cell inside its reach (MonsterVarieties `MaximumAttackDistance`: the Sand Spitter's
  30), moving inward only when that ring is full, instead of taking a melee contact cell (`MonsterSurround.Asker.Ranged`;
  live 2026-10-29: Spitters came in close). `MinimumAttackDistance` is not read yet.
  A give-up beyond twice the aggro range ends the client's chase action with S→C 0x1ac before its Move (GGG's server
  does the same: 925 of 1166 live give-ups carried one, 3.29 capture; a predictive client needs it, a lockstep one
  ignores it — [07](../docs/protocol/07-latency-visibility-movement.md) §11).
  Attack busy time covers the full attack duration; pending swing and stage generations prevent duplicate work.
  Three native AI-enable controls issued one chase each after 43, 38 and 50 ms.
  Four deliberate turns produced four chase orders at 249 ms intervals without duplicate orders between turns.
  Drowned attack starts remained 2432 and 2428 ms apart for a 2.4 s attack.
  Actual Escape-menu pause held 30 samples at one cell without actions; resume continued the existing path.
  Automated cadence, pause, interruption and stage-completion checks pass; quoted native controls are historical
  LocalExile evidence, not a fresh run of this standalone port.
  Code: `MonsterAiCadence.cs`, `Program.Monsters.cs`.
  Evidence: `artifacts/wand-ai-validation/ai-native-response-*.json`, `ai-native-turns.json`,
  `ai-native-attack-pacing.json` and `ai-native-menu-{paused,resumed}.json`.
  Docs: [11](../docs/protocol/11-skills.md) §6.
- Monsters fight back: their first granted effect as a non-pathing skill with MonsterVarieties attack time, reach and
  level damage; they swing (animated) when in reach and the player's life goes down after evasion, armour and block;
  only monsters with a critical strike chance from their data or mods crit (none from their data yet; of Deadliness);
  walking away dodges; death plays and blocks moving; the death screen (S→C 0x55) shows "Resurrect in Town" /
  "Resurrect at Checkpoint" (hardcore: "Exit"), and both buttons work (C→S 0x54; in town: the last town); `/heal`.
  Code: `MonsterAttack`, `Resurrect`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §6–7.
- The Shaper (`MapWorldsShapersRealm`, TheShaperBoss; `TheShaperBossUberElder` in the Uber Elder arena), the Elder
  (`MapWorldsElderArena`, TheElder; TheElderUber) and Sirus (`AtlasExilesBoss5` "Eye of the Storm", AtlasExile5;
  the Gauntlet and standalone rows fight the same way) fight with their own kits instead of one granted effect as a plain
  swing. All are scripted (the ordinary AI skips them), own their whole effect list on the actor under ids 0x4000-0x4005
  (Shaper), 0x4010-0x4015 (Elder) and 0x4020-0x402c (Sirus) — never a gem id — and play it from one controller per
  monster: the Shaper's melee (481), Celestial Beam (6093, a three-second degen whose fire/cold/lightning come from its
  per-minute stats), accelerating projectiles (6106), flicker slam (6091), retreat (6089) and the bullet-hell ring (6111,
  twelve in a circle) behind `immune_shield.ao` at 75/50/25%; the Elder's melee (406), siphon (9058, a physical degen
  that heals him a quarter of what it takes), slamming tentacles (9062, five portals around the player), expanding nova
  (9066), doom projectile (9351) and three-shot spellcast (9330) behind `eldershield` at the same thresholds; Sirus'
  beam blast (7008, his record's own skill — he never swings a weapon), corridor "Die" beam (10948, a five-unit corridor
  that hits 1.1 s into the cast), meteor arena (10141, eight circles then the desolation 10945 burning for four seconds),
  circle-maze sweep (10949-10953, five blasts of a turn each), multi-beam barrage (8098/8099 with the clone beam 7031)
  and beam bomb (10973) behind `atlas_orion_immunity` (row 1464) at the same thresholds, blinking out of a player's face
  and back into reach (the teleport clips are art, not actions: an action would land its contact too). The arena is a
  phased one (`MapBossFights`): WorldAreas lists the throne row first and the walking row as the held-back last boss, so
  a fresh arena holds only `AtlasExile5Throne` — Sirus seated, no granted effects of its own — and killing him is what
  forms the walking Sirus and his four storms. Both are Sirus and both fight; the seated one casts without leaving the
  throne. Damage comes from
  each effect's own stat set at the area's level, pacing from its clip's cast time; a timed hit lands on every player
  standing in it, not only on the connection that owns the fight, and an intermission returns the boss to the arena's
  own `ArenaMiddle` marker (the layout's spawn is the map entrance, which put the Shaper at the door). Sirus' rows carry it as a fraction of
  the variety's damage (0.8-2.4, read as a multiplier, not as the flat hit the rounded row would suggest); every `.ao`
  path is checked against the client's metadata. Not yet: the Shaper's fragment summon (3429 names a GameObject, not a
  monster variety), his teleport-the-player (6092) and portal warp (6090), the uber phase variants (6112-6114, 7905), the
  Elder's super siphon (9061), the Uber Elder pairing's shared phases (each boss runs its own), Sirus' death storms and
  the apparitions/clones (separate monsters and encounters) and his meteor's per-effect ground layers. Code:
  `Modules/Core/Game/Monster/AtlasBosses.cs`, `AtlasBossFight.cs`, `ShaperFight.cs`, `ElderFight.cs`, `SirusFight.cs`.
- Dominus (Upper Sceptre of God): both forms fight to the end without a client crash (live 2026-11-05).
  He swings `DominusLightningSlam` (bond, mark and souls effects are skipped); a far boss is created asleep and
  woken when the player comes near. Code: `MavenNucleus.CombatEffect`, `SpawnMonster`.
  Docs: [11](../docs/protocol/11-skills.md) §6.

## Partly working

- The campaign Trinity has native attacks and two scorpion phases; exact official AI weights remain unverified.
  Docs: [Act 9](../docs/game/campaign/act9.md).
- Act 10 Kitava: three hearts, finale and manual Epilogue portal; AI provisional ([Act 10](../docs/game/campaign/act10.md)).
- Act 5 Kitava: body/heart sequence and rescue; AI tuning provisional ([Act 5](../docs/game/campaign/act5.md)).
- Doedre: cauldron and effluent modes; AI tuning provisional ([Act 8](../docs/game/campaign/act8.md)).

- Vaal Oversoul: campaign encounter; AI timing uncalibrated ([Act 2](../docs/game/campaign/act2.md)).
- The King in the Mists' Crux of Nothingness encounter: `An Audience With The King` alone opens the area in the map
  device; the humanoid form fights with the default attack, spell projectile, Vine Slam and Vine Cascade, spawns the three
  Mystic Fetishes as visible shrines, teleports next to them and channels its `0x400e` ritual into one (the fetish is
  immune through the whole ritual animation and becomes attackable at `call_end_01`; the King is immune throughout;
  live 2026-11-07: three rituals, no crash), then returns to the centre (Portent Vapors stacks), summons Plagued Rats
  and an effigy. The fetish/shrine StateMachine `active` states are not sent yet; the central `RitualPillar2` shrine
  is created but its `active_pillars` animation is not advanced.
  Fetish rituals are life-gated at 92/73/55% (one ritual per hit, immunity for the channel); the humanoid is
  phase-locked at 50%, slams its staff and teleports the player into the same-area hedge maze (wisps, exit portal and
  Branching Affliction). On the return the husk is set to the official 25% life and becomes the treant
  (`VoodooKingBoss3`, full 25.2M life) with a 7 s emergence grace, Treant Slam, Bug Bomb, Bugrend, effigy summons and a
  walk that follows a kiting player. The kill
  rolls the guaranteed unique (with That Which Was Taken paired with The Light of Meaning), Hextoad/Hexpass and The
  Endless Darkness.
  The 2026-10-31 official 3.29.3b capture (docs, "Official 3.29.3b capture") showed the real fight differs. The
  fetish ritual is the client's `GSVoodooKingRitual` `0x400e` channel with server-side hazards (its crash was the
  record's missing `skill_behaviour_variation`, [king-in-the-mists](../docs/game/king-in-the-mists.md)). The fetishes are hostile and destructible (no experience or loot); the
  central shrine object exists but its `active_pillars` animation is not advanced yet; the maze uses a room-graph route
  with real `GuidingLightDaemonMaze_` actors plus a werewolf and spider; the treant uses Meditation/Dance/Punishment;
  BugRend is the official `VoodooKingBugRend`/`BugRendSingle` + `BugSoulrend` with six projectiles; the emergence grace
  is 7 s and the morph waits for the full clip. Teleport and return actions move the object at departure and end before
  the next action, so the model is not left in a teleport/channel pose. The official bark pools are wired to their skills
  (Vine Cascade/Thorngrip, Vine Slam/Sprout-Invasive growth-Grow, effigy/Agrik armach-Dark effigy, the two hexes, the
  ritual line at `call_start_01`, the maze lines); the dash/strafe settle now uses a 0.1 s `0x1ba` with the
  follow-terrain marker instead of 5000 s, which the client had silently ignored (the cause of the 2026-11-01 position
  drift). The dash clips move 24 cells (not 19) and the strafe direction now follows the clip's own convention
  (`move_left` = `(dy, -dx)`, `move_right` = `(-dy, dx)` relative to the player-to-King vector), with a `0x1b9`
  facing the player before each clip so a leftover cast facing cannot skew it, and both the facing and the landing
  cell are computed at the send gate from the player cell the action carries (a fast player dash during the 120 ms
  action-end settle otherwise made the clip and settle disagree). The spell projectile's cast step (~12 cells toward
  the target) is adopted on the server too, so the next dash starts from the model's cell instead of 12+ cells behind.
  The dash/strafe action now holds 0.65 s before its settle (the client starts the 0.5 s clip late), and the previous
  action ends 220 ms earlier, so the clip is not cut at a fraction of its move. An approach blink jump is capped at 24
  cells instead of teleporting 34+ cells from a 50-cell gap.
  The `0x2909` step
  glides both sides with `0x1ba` instead of teleporting only the server. The blink-away now fires after two dashes
  instead of waiting behind a ready strafe, and the spell projectile is checked before movement, so the King keeps
  casting between the escapes. A re-entered kept instance puts the phase-1 humanoid back on the mapboss point before
  the intro (it used to start on its saved cell, e.g. 45 cells south of the centre). The generic monster collision
  estimate no longer moves either King form: it changed only the server cell without a packet, so a strafe settle near
  a melee player was reverted and the model snapped back to the ramp start. Some backward dashes still settle with
  20-41-cell corrections when the client cuts its move clip; the measured table is in PR #425.
  Not yet: blood/caustic ground objects, the Ritual imprisonment/maim movement rule, the phase-trigger confirmation,
  ground hazards, monster energy shield, Shadow Surge/Mystic Miasma damage over time, corpse items, the Unlight
  Altar/Bloodline and the remaining native animation captures.
  Code: `Modules/Core/Game/Monster/KingInMists.cs`, `ItemDrops.KingInMists`, `MapDevice`.
  Docs: [king-in-the-mists](../docs/game/king-in-the-mists.md).
- Inherited Life corpse eligibility and sinking flags flow from object-types manifest v4 through `MonsterCatalog`
  into ordinary monster creation.
  The combined object-types format retains the stationary collision footprints and models alongside Life data.
  Explicit false, native defaults, missing inheritance and the Player veto have focused extraction and Core tests.
  The ordinary corpse path requires zero Life; a life-1 boss stage remains alive, with sinking handled independently.
  Six integrated native cases at source `dbfe717`, recorded by `53edc77`, cover ordinary alive/dead, Raised Zombie
  false, Hillock life 1, sinking and an owned Spectral Wolf forced false by that source's separate summon runtime.
  This branch preserves the ordinary flag math and fields; it has no independent native pass or owned-actor runtime.
  The Core helper retains an optional owned guard; corpse identity and final-death registration remain open.
  Deathrattle's warcry explosion is the first consumer: a dead zero-Life eligible monster explodes once and is marked
  `SpawnedMonster.CorpseConsumed` and removed ([11](../docs/protocol/11-skills.md) section 10i).
  Code: `MonsterLifeData.cs`, `MonsterObject`, `MonsterCatalog.cs`, `Program.Monsters.cs`, `ot_components.py`.
  Docs: [04](../docs/protocol/04-player-entity.md#44-life-0x141e72890-55-bytes).
- Player hits on monsters: each one records the time on the monster (`PlayerHitAt`), and the player's death clears it.
  `enemy_life_regeneration_rate_+%_for_4_seconds_on_hit` (Devout of Ryslatha) scales a monster's regeneration for 4 s
  after a hit.
  Gateway coverage only; not checked with the real client.
  Code: `Program.MonsterMods.cs`, `Program.Combat.cs`.
  Docs: [14](../docs/protocol/14-monster-packs.md) §5, [Polytheist](../docs/game/polytheist.md).
- Monster hits on the player: an attack that is neither evaded nor blocked records the time on the monster
  (`HitPlayerAt`), and the player's death clears it.
  For 4 s after it, the player's `enemies_that_hit_you_with_attack_recently_attack_speed_+%` (Devout of Gruthkul)
  lengthens the monster's swings, and its attack stats are sent again on its Stats component.
  Gateway coverage only; whether the client's swing animation follows those stats is not checked with the real client.
  Code: `Program.Monsters.cs` (`MonsterSwingSeconds`, `SendAttackerSlows`), `MonsterMods.cs`.
  Docs: [11](../docs/protocol/11-skills.md) §6, [Polytheist](../docs/game/polytheist.md).
- Monster walk speed on the server includes the client's per-monster speed roll: Actor setup draws a uniform percent
  in the MonsterVarieties row's range (the Goatmen ±5 %, the 7.60–8.40 band measured live for a base of 8; most rows
  none) from a TinyMT32 state keyed by the object id and its type, and the walk estimate, swing range and contact
  checks use that speed; equal bounds retain their fixed percent rather than defaulting to zero.
  The range, the generator and the key order come from the decompile and pass the tests; whether the key's type word
  is the 0x2bb type hash, and so whether the exact value matches the client's, is not checked live yet
  (`scripts/re/frida/trace-movement.js` prints each monster's seed words, range and multiplier).
  Code: `MonsterSpeedRoll.cs`, `AnimationVariantSelector.cs`, `MonsterCatalog.cs`, Gateway (`SpawnMonster`).
  Docs: [07](../docs/protocol/07-latency-visibility-movement.md) §5 ("Monsters on the client").
- Monster mods' accuracy (of the Marksman, of Precision): the monster's accuracy is raised in its swing, but only the
  tests check it; live, the player was never evaded.
  Code: `MonsterMods.cs`.
  Docs: [14](../docs/protocol/14-monster-packs.md) §5.
- Monster summoning skills: a monster whose skill brings monsters (the stat set's `alternate_minion`, a
  SummonedSpecificMonsters row; the Labyrinth's Immolator) uses it within its aggro range, at the skill's cast time and
  cooldown, while fewer than five of its monsters live (our cap); at the action's contact its
  `number_of_monsters_to_summon` appear `summon_specific_monsters_in_front_offset` ahead of it, towards the player,
  hostile, giving no experience or items when the skill says so, and not kept in the instance; the summoner plays its
  actor's `SpellAreaOfEffect` clip (0x1d1), as the skill's own action plays nothing on the client.
  Built for the Crematorium trial: its Immolators' skeletons checked live (2026-11-01), their opening being checked.
  Code: `SkillCatalog.MonsterSummon`, `Program.Monsters.cs` (`MonsterSummon`), Gateway.
  Docs: [labyrinth-trials](../docs/game/labyrinth-trials.md) ("Monsters").
- Monster flee: a player hit with a chance to cause monsters to flee (Surfcaster's small nodes, Chance to Flee Support)
  makes a living monster walk to a floor cell 5 m away from the player and not attack until that walk ends.
  Magic monsters avoid half of the flees and rare and unique ones all, through their rarity's own mods.
  Only the Gateway tests check it; the distance and the action the real server walks it with are not verified.
  Code: `MonsterFlee.cs`, `MonsterAilments.cs`, `Program.Monsters.cs`.
  Docs: [11](../docs/protocol/11-skills.md) §6, [Surfcaster](../docs/game/surfcaster.md).

## Not working yet

- Area monsters: pack counts, magic/rare chances and rare mod counts are our own numbers;
  Act 1 Twilight Strand caps new populations at a provisional 45 including Hillock and the tutorial zombie.
  The reference capture misses its opening, so this is calibration rather than a verified official total;
  74 monster mods need systems the server lacks (auras on allies, monster skills, ailments on the player …) and do
  not roll;
  Drowned ignore their ground types; how near the player must come before an emerge monster rises is our choice;
  the boss life bar and boss stages exist for Hillock only (`BossFights.cs`;
  other bosses with `BossHealthBar` get the bar too); monsters respawn on every entry;
  a new (area, seed) needs a `-Walkmaps` visit for its unique boss ([14](../docs/protocol/14-monster-packs.md) §4).
  Act 2's bandit lords (Kraityn, Alira, Oak) are created hostile without their help-or-kill dialogue
  ([17](../docs/protocol/17-npcs.md)).
  Not placed: Marceus the Defaced (a tile's statue spawner), The Solaris Temple Level 2's three Banners (a room the
  layouts never place) and Thistlesage (no room names his pack) ([14](../docs/protocol/14-monster-packs.md) §3).
  A boss's later form (Abberath2, Shakari2, Malachai's second form) is left to a fight or quest
  that does not create it yet.
