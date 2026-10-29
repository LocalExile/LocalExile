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
  (red) or friendly, that chases the player when near (Move action 0x1ab, client pathing).
  Code: `MonsterObject`, `MonsterCatalog.cs`, Gateway.
  Docs: [10](../docs/protocol/10-chat-and-commands.md) §5.
- Monster packs in areas: on entry the area fills with its MonsterPacks at the area level (The Coast: crab and Drowned
  packs, some magic, at least 2 with a rare; cannibal packs at the bonfires) and the unique boss at the camp's
  `mapboss` point with its boss pack (Fire Fury beside the campfire, verified live 2026-10-18);
  room points are found by fitting rooms to the doodads `-Walkmaps` captures.
  The Tidal Island's boat rooms are too sparse to fit and use their captured placement: Hailrake stands on the wreck.
  Code: `MonsterPacks.cs`, Gateway (`PopulateArea`), `scripts/walkmaps`, `scripts/re/atlas room-points`.
  Docs: [14](../docs/protocol/14-monster-packs.md).
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
- Monster hit reaction: a hit that leaves a monster alive and takes at least a tenth of its max life plays one of its
  rig's TakeHit clips with the clip's own sound (S→C 0x1b2 kind 0;
  a pool picks one at random), and not again until that clip has played (at least 0.4 s);
  a killing blow gets no flinch.
  Verified live 2026-10-25 on the Sanctum zombie and the Goatman (melee and Holy Strike);
  the damage and clip-length gates are not yet checked live.
  The weapon impact sound and the target's blood come from S→C 0x1af "action hit", sent on melee and attack-gem hits
  and on monster hits on the player (2026-10-25; not yet checked live after the timing cap).
  Code: `HitReaction.cs`, `MonsterCatalog.TakeHit`, Gateway (`SendFlinch`), `scripts/re/monster_takehit.py` →
  `data/monster-takehit.json`.
  Docs: [11](../docs/protocol/11-skills.md).
- Magic and rare monsters roll their mods from Mods.dat (monster domain, spawn weights by the monster's tags, level,
  one per family): magic one affix shared by the monsters of one name in an instance, rare two to four (poewiki
  "Rarity"), with a rare name from the Words.dat monster lists.
  The client shows them on hover (verified live 2026-10-25: "Gorewalker" / "Extra Life", "Quick Hungry Corpse" /
  "Hasted").
  Their life, hit, attack time, walk (sent as movement_velocity_+%, the client walks at the same speed), resistances,
  physical reduction, block, evasion, critical strikes and regeneration act on the server (each checked live); the
  instance keeps them.
  Only the 37 mods the server can honour roll; the other 74 are listed in [14](../docs/protocol/14-monster-packs.md)
  §5.
  Code: `MonsterMods.cs`, `Program.MonsterMods.cs`, `MagicProperties.cs`.
  Docs: [14](../docs/protocol/14-monster-packs.md) §5.
- Monsters fight back: their first granted effect as a non-pathing skill with MonsterVarieties attack time, reach and
  level damage; they swing (animated) when in reach and the player's life goes down after evasion, armour and block;
  only monsters with a critical strike chance from their data or mods crit (none from their data yet; of Deadliness);
  walking away dodges; death plays and blocks moving; the death screen (S→C 0x55) shows "Resurrect in Town" /
  "Resurrect at Checkpoint" (hardcore: "Exit"), and both buttons work (C→S 0x54; in town: the last town); `/heal`.
  Code: `MonsterAttack`, `Resurrect`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §6–7.

## Partly working

- Inherited Life corpse eligibility and sinking flags flow from object-types manifest v4 through `MonsterCatalog`
  into ordinary monster creation.
  The combined object-types format retains the stationary collision footprints and models alongside Life data.
  Explicit false, native defaults, missing inheritance and the Player veto have focused extraction and Core tests.
  The ordinary corpse path requires zero Life; a life-1 boss stage remains alive, with sinking handled independently.
  Six integrated native cases at source `dbfe717`, recorded by `53edc77`, cover ordinary alive/dead, Raised Zombie
  false, Hillock life 1, sinking and an owned Spectral Wolf forced false by that source's separate summon runtime.
  This branch preserves the ordinary flag math and fields; it has no independent native pass or owned-actor runtime.
  The Core helper retains an optional owned guard; corpse identity, final-death registration and consumption remain open.
  Code: `MonsterLifeData.cs`, `MonsterObject`, `MonsterCatalog.cs`, `Program.Monsters.cs`, `ot_components.py`.
  Docs: [04](../docs/protocol/04-player-entity.md#44-life-0x141e6c2f0-55-bytes).
- Player hits on monsters: each one records the time on the monster (`PlayerHitAt`), and the player's death clears it.
  `enemy_life_regeneration_rate_+%_for_4_seconds_on_hit` (Devout of Ryslatha) scales a monster's regeneration for 4 s
  after a hit.
  Gateway coverage only; not checked with the real client.
  Code: `Program.MonsterMods.cs`, `Program.Combat.cs`.
  Docs: [14](../docs/protocol/14-monster-packs.md) §5, [Polytheist](../docs/game/polytheist.md).
- Monster mods' accuracy (of the Marksman, of Precision): the monster's accuracy is raised in its swing, but only the
  tests check it; live, the player was never evaded.
  Code: `MonsterMods.cs`.
  Docs: [14](../docs/protocol/14-monster-packs.md) §5.

## Not working yet

- Area monsters: pack counts, magic/rare chances and rare mod counts are our own numbers;
  74 monster mods need systems the server lacks (auras on allies, monster skills, ailments on the player …) and do
  not roll;
  Drowned ignore their ground types; how near the player must come before an emerge monster rises is our choice;
  the boss life bar and boss stages exist for Hillock only (`BossFights.cs`;
  other bosses with `BossHealthBar` get the bar too); monsters respawn on every entry;
  a new (area, seed) needs a `-Walkmaps` visit for its unique boss ([14](../docs/protocol/14-monster-packs.md) §4).
