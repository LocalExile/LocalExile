# Roadmap

What to build next, for anyone (person or agent) who wants to help. What already works is in [Status.md](Status.md).

## How to pick up an item

1. Open a GitHub issue named after the item (or comment on an existing one) so two people don't do the same work.
2. Read the docs the item links to. Most items start with reverse engineering: find how the client reads a message,
   then send it from the server. [docs/re/tooling.md](docs/re/tooling.md) has the workflow and recipes.
3. Verify with the real client through `.\scripts\dev\live.ps1`, and check the logs in `logs\`. Add tests for any
   message layout you implement (see `tests/GameServer.Core.Tests`).
4. In the same pull request: document what you found in `docs/protocol/` (new message ids, layouts, client addresses
   in [address-index.md](docs/protocol/address-index.md)), move the item to [Status.md](Status.md), and remove or
   update it here.

Items are in rough priority order within each section. "Known" says what is already found; "Start" says where to look.

## 1. Quality of life

Aura intent persistence, town suppression and death reset are implemented; validate town/combat/character-switch round trips in the real client (protocol/11-skills.md).

Harvest station placement and native recipe browsing are implemented. Next: implement Harvest recipe execution and atomic lifeforce payment; see protocol/21-hideouts.md.

### Switching hideout templates
- **Known:** Helena now stands to Niko's screen-right and opens HideoutSelectionWindow (0x7b kind 0x22).
  The owned list contains the default Lush Hideout. [21](docs/protocol/21-hideouts.md).
- **Start:** map the selection request, persist the chosen template, and use it for /hideout, player keys and portals.

The ordinary crafting bench's active recipes, unlocks and inventory/personal-stash payment
are implemented (Status and protocol/21-hideouts.md). Disabled legacy recipes, map-device
recipes and other specialised crafting stations are separate from the ordinary bench.

Essence item crafting is implemented (105 currencies, class-specific guaranteed modifiers;
see Status and protocol/13-items.md). Imprisoned essence encounters, Remnant of Corruption
on monsters and stash-tab essence upgrading remain separate features.

### Chat lines and the global channel
- **Known:** C→S 0x15 (chat text) is parsed; command replies use S→C 0x17 (chat line). 0x12d shows BackendErrors rows, 0x157 is
  a PvP challenge, 0x26 disconnects. The client logs "Timeout while joining global chat channel 820".
- **Start:** [10](docs/protocol/10-chat-and-commands.md) §2 and §6. Find the S→C message that adds a line (sender + text) to the
  chat panel, and the channel join request and its answer.
- **Done when:** command replies and server messages appear as chat lines, and the channel join stops timing out.

### A server tick loop
- Continuous player recovery has its own 16 ms elapsed-game-time loop (regeneration, flasks and self RF);
  implemented, awaiting real-client visual verification. See Status and protocol/20-damage.md.
- **Known:** `Program.TickRate` (30/s) only bounds attack times; hits use timers, the AI a 500 ms loop.
- **Done when:** one 30 Hz loop per instance drives movement estimates, AI and combat.

Delve slot bounds, empty resonator socket layouts and stackable migration are implemented; verify the corrected fossil/resonator presentation in the real client (18-stash.md).

### Stash: tab settings and the other special tabs
- **Known:** the stash opens on the last tab, tabs load and items move by click and Ctrl + click
  ([18](docs/protocol/18-stash.md)); special tabs hold only their own items, the currency, essence and fragment tabs each
  item in its slot, and affinities (0xd3) are saved (18 §7). Switching to a loaded tab sends nothing to the server.
- **Start:** check live that the affinity window closes on S→C 0x136 and that currency Ctrl + click (0xe1) lands in its
  slot; tab rename / colour (0xd2), order (0xcc) and folders (0xd1); the unique and divination tabs.
- **Done when:** tab settings are kept and every special tab takes its items as the real one does.

## 2. The world

### Every area's layout at seed 1
- **Known:** generated areas only exist in the client; `live.ps1 -Walkmaps` captures an area's walk map, area
  transitions and waypoint when the client generates it ([08](docs/protocol/08-area-transitions.md) §7). About 30
  areas are captured; waypoint travel to an uncaptured one (The Submerged Passage) puts the player outside the map
  ([16](docs/protocol/16-waypoints.md) "Open").
- **Start:** a dev command that walks the WorldAreas list with `/goto` and waits for each capture.
- **Done when:** every act area has its seed-1 capture, and every waypoint area its waypoint in it.

### Area transitions without a walk-map run
- **Known:** every area gets its area transitions and destinations, but a generated area only has them once
  `live.ps1 -Walkmaps` captured its layout for that seed ([08](docs/protocol/08-area-transitions.md) §7). 11 atlas
  connections are unresolved, 41 are point-only, and four object types are sent as plain AreaTransition.
- **Start:** reproduce the client's level generator (the atlas already parses graphs, rooms and tiles), or ship
  captures for fixed seeds.
- **Done when:** a new seed gets its area transitions without Frida.

### Quests: Breaking Some Eggs (a1q4), the Submerged Passage
- **Known:** the whole chain is in [12](docs/protocol/12-quests.md): three Rhoa Nest chests drop three glyphs, the Glyph
  Wall takes them, quest flag `a1q4-PlacedGlyphs` removes the client-side `SunkenPassageBlocker` wall (the invisible
  wall in front of the cave today) and the wall and waterfall play their Transitionable animations.
- **Needs:** items (Roadmap "Items and inventories"), the Chest component's blob, a Transitionable state change. The
  S→C quest flag message is known: 0x2ba to the Player component, update flag 0x02 ([16](docs/protocol/16-waypoints.md) §3).
- **Start:** `/quest_flag a1q4-PlacedGlyphs` (it reaches the client's quest flags, [12](docs/protocol/12-quests.md) §5):
  check that it alone opens the passage. Then a quest script for a1q4.
- **Done when:** the glyph quest can be played through and the Submerged Passage opens.

### Monsters: beyond `/spawn_mob`
- **Known:** spawning, levels, rarity, names, team, life, StateMachine / Transitionable types, walking, chasing and
  melee attacks all work ([10](docs/protocol/10-chat-and-commands.md) §5, [11](docs/protocol/11-skills.md) §6); areas
  are populated with their MonsterPacks and unique boss ([14](docs/protocol/14-monster-packs.md)).
- **Start:** the "emerge" action for emerge monsters (then add the skipped emerge packs), mods for magic/rare monsters
  (their life bonus is done), stages of other bosses (`BossFights.cs` has Hillock's), pack `Grounds`, the 346 varieties with NPC / ControlZone / BlightTower … components (each needs its
  deserializer, schema.md). Check every new component with `scripts/re/frida/check-components.js`.
- **Done when:** monsters in areas match the official game's (mods, rarity bonuses, emerge, boss bar).

### Maps and the Atlas (3.29)
- **Known:** the 3.29 rules and the 20 generic map bases (`MapKeyTier1` … `MapKeyTier16` and four special ones) are in
  [game/maps.md](docs/game/maps.md). The map device and Nessa's shop use the older area-named maps (Beach Map).
- **Start:** the Atlas panel's messages and how it stores progress; the map stash tab's layout (18 §7); maps from the
  generic bases on the layout picked on the Atlas; drops by tier.
- **Done when:** maps drop, are stored and run the 3.29 way, and the Atlas shows and saves progress.

### Hideouts: decorations, editing and the map device
- **Known:** the Lush Hideout works: travel, waypoint, stash, Nessa, the Edit button and the map device: slots, Activate
  and six portals into the map ([21](docs/protocol/21-hideouts.md) §5). Its decoration (`data/hideouts.json`) lists every doodad with its type and model.
- **Start:** create the decorations (`HideoutObject`, each type's components from data/object-types.json); the Edit
  mode's client messages (move, place, remove, save); maps: the arrival point, a return portal, fragments and device
  recipes and map mods. Personal template selection and login to the selected home are implemented (see Status).
- **Done when:** the hideout looks like the real one, can be edited and saved, and maps can be run from its device.

### NPCs: dialogue options, shops and the other towns
- **Known:** NPC objects, Lioneye's Watch placements and the dialogue window (S→C 0x84, C→S 0x85) work
  ([17](docs/protocol/17-npcs.md)). The client picks the NPCTalk row itself (`0x141e76d40`).
- **Start:** pick a dialogue option live and read the C→S message; NPCDialogueWindow's option handler (`0x140723d60`,
  `0x14071f6d0`). Shops from NPCShop / ShopSets. Placements for the other towns (`NpcCatalog.Towns`, with `/pos`).
- **Done when:** talk options play their text, Nessa and Tarkleigh trade, and every town has its NPCs.

## 3. Gameplay

- **Ordinary aura activation:** nine previously rejected self auras now share reservation, presentation and
  the supported stat/damage paths. Remaining work: Discipline's ES pool, Pride's enemy debuff, conditional
  consecrated ground, herald/minion/banner mechanics. See protocol/11-skills.md section 13.

- **Aura caster art:** the distinct EPKFiles2 visuals are selected on activation and area restoration;
  the live source-only regression is addressed with a separate received buff for the top-left icon,
  awaiting confirmation. Radius-scaled aura rings, if added, still need their actual radius.

- **Completed area fix:** Cyclone's `/set_aoe` final radius is now sent to the client and shared with hit selection;
  see Status and protocol/11-skills.md (live check pending).

### The player's real stats
- **Known:** client stat key = Stats.dat row + 1 ([11](docs/protocol/11-skills.md) §8); `/set_stat` takes names. The
  server must send final values (the client derives none): the attack's are computed in `SkillCatalog.Unarmed.Stats`.
- **Done when:** life, mana, attributes, resistances and their derived values come from the server's own calculation
  (Characters.dat base values, level, items, passives) and are sent in the Stats component.

### Character progression: the rest of the Passive Skill Tree
- **Complete ordinary/Phrecia ascendancy mechanics and acquisition:** the shared 21+19 choice/points/save lifecycle
  and native per-character graph selector are implemented ([Status](Status.md), [usage/limitations](docs/game/ascendancies.md)).
  Preserve the ordinary topology in the generated Phrecia client graph. Next: implement Labyrinth/Altar selection and
  awards, support-gem execution, incoming ailments/stuns and the remaining signature subsystems in
  [the all-19 dependency waves](docs/game/phrecia-ascendancies.md#dependencies-of-all-19-classes). Each class needs actual
  gameplay evidence for its signature mechanics; a selectable tree or displayed stat is insufficient.
- **Known:** experience, levels, S→C 0x33 and C→S 0x34 (allocate) work ([15](docs/protocol/15-progression.md)).
- **Known:** gold respec at Bestel (C→S 0x36 / 0x37) and masteries (0x35) work.
- **Known:** refunds with refund points (0x36 flag 0; 0x33's first u16) work; Orbs of Regret (right-click, 0x58) add them.
- **Accuracy Mastery:** 3 of its 6 effects are modelled ([Status](Status.md), [11](docs/protocol/11-skills.md) §4–5):
  Accuracy = Intelligence, +3 per Dexterity instead of +2, and +500 / -2 per level. Not modelled: 40% increased
  Accuracy against Unique Enemies, 50% increased against Enemies in Close Range, and Minions' Accuracy equals yours
  (needs target rarity/distance and minions).
- **Start:** quest refund points, ascendancy points, quest points (0x33 +0x8a); the tree's connections
  (PassiveSkillGraph.psg) so the server checks them too.
- **Done when:** passives can be refunded with points from quests, and ascendancies chosen.

### Auras: reservation and effects
- **Known:** `GrantedEffectsPerLevel` carries the reservation; the Life pool's reserved field and S→C 0x1b1 update it;
  Purity of Elements/Fire/Ice/Lightning, Clarity, Vitality, Precision and Haste are implemented with their buff
  visuals and `/auras`, awaiting live verification ([11](docs/protocol/11-skills.md) 13, [Status](Status.md)).
- **Start:** the auras' 3D effects (BuffVisuals EPKs of the `IsSkillBuff` rows do not render, unlike Righteous Fire's);
  the combat auras (Anger, Hatred, Wrath, Zealotry, Pride), Heralds, banners, curse auras and
   Determination/Grace/Discipline's armour/evasion/ES effects. The skill-bar indicator's buff association is implemented
   (0x1c1 bit3; live check pending, see Status). Support gems come with the gem system ("Gems" below); party and
  minion auras and life reservation with their own systems.
- **Done when:** the standard auras reserve correctly, their effects match PoB, and the character panel and HUD show the
  same reserved and unreserved values.

### Items: mods, rarity, using flasks and currency
- **Jeweller's Orb:** initial local 0x71/Shift 0x72 implementation rerolls counts, colours and links with class and
  item-level caps; the live-found refusal at the item-level cap is fixed (only base capacity blocks use), verified
  live. See 13-items.md, "Jeweller's Orb" for the requested local rules and remaining exact-game behaviour.
- **Orb of Fusing:** normal/Shift handling and link-only rerolls are implemented and verified live. Exact
  link probabilities, quality scaling and special item overrides remain open (13-items.md, "Orb of Fusing").
- **Known:** items serialize, move, socket and drop ([13](docs/protocol/13-items.md)); rarity, implicits and every mod
  list are sent and saved (13 §2a); magic and rare items roll, drop unidentified and a Scroll of Wisdom identifies
  them (13 §2b); equipped items' mods count on the character (13 §2c). Open issues: enchants / crafted / corrupted / influences / uniques (#60), area and monster mods (#61).
  The Mud Flats glyphs are quest items (`Metadata/Items/QuestItems/Glyphs/Glyph1..3`, [12](docs/protocol/12-quests.md)).
- **Start:** other currency through C→S 0x71 (13 §4: Orb of Transmutation, Alteration, Augmentation, Regal, Alchemy,
  Chaos, Scouring can reuse `ItemRolls`); items' base weapon damage and armour / evasion / energy shield (then their
  local mods, 13 §2c); flask charges after use.
- **Socket colours (3.29 rules):** any gem goes into any socket, whatever the colours, and a gem in a red, green or blue
  socket of its own colour gets +10% quality. Sockets are white by default; each has a small chance to be red, green or
  blue instead, in proportion to the item's attribute requirements, and that chance rises with item level. Chromatic Orbs
  force one socket to be coloured and roll the rest normally (more quality, more coloured sockets). Jeweller's Orbs, Orbs
  of Binding and the Crafting Bench add white sockets. Vaal Orb corruption turns a white socket coloured, and the Locus
  of Corruption turns all white sockets coloured. Today `ItemCatalog.DefaultSockets` makes every socket the item's main
  attribute colour, which is the pre-3.29 look.
- **Done when:** items' damage and defences count, and flasks and currency can be used.

### Damage model (foundation done, deeper defence next)
- **Done:** hits are per-type `DamagePacket`s; conversion follows physical, lightning, cold, fire, chaos, including
  chained conversion, the 100% cap and gain-as-extra. DoT/ailment formulas and fractional ticks are in the same model,
  and per-type resistance mitigation works on packets. The default attack, Fireball and monster melee use packets and
  read the player's/skill's conversion stats ([20](docs/protocol/20-damage.md)). The player's real life, resistances,
  armour and evasion are computed from level, passives and base equipment, and armour/evasion mitigate monster hits
  (Status: Armour and Evasion).
- **Known:** both sides still have flat damage ranges ([11](docs/protocol/11-skills.md) 5, 6); energy shield,
  penetration, exposure, ailment application, added damage and support modifiers are not modelled.
- **Start:** energy shield, penetration/exposure, the remaining conditional critical strike stats from the stats both
  sides already carry; monster skills beyond the first granted effect (ranged, spells). Add support effects through
  `DamagePacket.Add`, `Scale`, `Convert` and `AddGain`.
- **Done when:** damage numbers match the character panel and the monsters' levels.

### Death screen variants
- **Known:** S→C 0x55 flags bit0/2/3 are mapped ([11](docs/protocol/11-skills.md) §7); bit4 and the trailing u32 / u8 /
  u16 are not.
- **Done when:** Sanctum and other modes show their own options; hardcore's "Exit" works (with Esc → Exit, C→S 0x153).

### Quests
- **Known:** the quest engine and a1q1 "Enemy at the Gate" ([12](docs/protocol/12-quests.md) §5–6): quest flags saved
  per character, one script per quest (events → flags; flags → exits), S→C 0x2ba steps a Transitionable.
- **Start:** the next quests act by act, each checked against the real game's behavior. The Strand's visual pieces
  (the gate's animation, the Doorman; [12](docs/protocol/12-quests.md) §6) are low priority.
  a1q4's Glyph Wall (§1–4) is the next world-changing one.
- **Done when:** the campaign's quests can be played through, with their world changes, logs and rewards.

### Gems: how they work
The gem system as agreed on 2026-10-20, built in the parts below (#62, #63, #64). What is known about the client and the
data is in [19](docs/protocol/19-gems.md).

- **A gem item** is a base type and a variant: the normal gem; a transfigured ("of X") or Trarthan ("of Trarthus")
  variant, which acts as its own skill with its own effect, tags and mechanics; or a Vaal gem, which grants the regular
  Vaal skill and its base skill (transfigured or Trarthan if its variant is). A gem's level comes from its experience,
  up to the cap of its experience type: 20 for most gems, 3 for exceptional supports, 4 for Awakened Empower,
  Enhance and Enlighten, 5 for the other awakened supports, 1 for single-level gems. Corruption and bonus levels can
  go higher, never past 40.
- **Rules:** socketing a gem whose level or attribute requirements aren't met is refused. A socketed gem that stops
  meeting them is disabled: its skill stays on the skill bar but can't be used, its supports don't apply, its level-up
  icon is greyed, and the gem stays in its socket. Each socketed skill gem is its own skill, with an id kept while it
  stays socketed; an aura ends when its gem leaves. Support gems are never granted as skills. Awakened supports other
  than Empower, Enhance and Enlighten (disabled since 3.28), the "[DNT]" placeholder variants and the gem rows named
  "[UNUSED] …" or "WIP …" are never created.

### Gems 1: gem levels, stable skill ids and requirements (#62)
- **Known:** each socketed gem keeps its skill id while it stays socketed, and an aura or Righteous Fire ends with its
  gem (part (a)). Part (b): a gem's skill uses its level (from its experience) and quality, transfigured, Trarthan and
  Vaal gems grant their skills, the variant is the SkillGem data's u32, and `/gem` makes any gem
  ([19](docs/protocol/19-gems.md) §1, §3); not yet checked with the real client. Part (c), the requirements, is in:
  the level is `PlayerLevelReq` (Haste 24 at gem level 1 … 70 at 20, as the client's tooltips) and the attributes are
  the client's own formula, fitted from `0x14246fc40` and checked against 570 poedb tooltip cells with no mismatch
  (`trunc((3·level_req + 11)·(percent/100)^0.9·k + 0.5)`, `k` 0.5 for supports and 0.7 for skills, none below 14). The
  level stops a gem's experience at the first one the character does not meet; socketing below either is refused, and a
  gem the character is below (after `/set_level` down) makes its item inactive: red on the client, its mods and gems not
  counting, its skill gone from the list so it cannot be cast, and an inactive weapon falls back to the unarmed attack
  (Status "Gem requirements"). Checked live 2026-10-23 for the level and the attributes (`/set_stat intelligence` above
  and below the 155 a level 20 Fireball needs: at 130 the client reds the gem and the wand's mods stop counting). Left
  open: the client still draws an inactive item worn (red wand in hand) — check with the official client what it does
  there, and whether to move the item out of the row instead ([13](docs/protocol/13-items.md) §2d).
- **Start:** check part (b) live (a level 20 Fireball's damage and cost, a quality aura, Ice Nova of Frostbolts, a Vaal
  gem's two skills).
- **Done when:** a `/gem` gem of any level and quality casts with that level's numbers, transfigured and Vaal gems grant
  the right skills, skill ids don't change when other gems move, and requirements match the tooltips and are enforced.

### Gems 3: support gems and links (#64)
- **Known:** the skill record has a support list ([11](docs/protocol/11-skills.md) §3); granting a support as a skill
  crashes the client ([13](docs/protocol/13-items.md) §6). GrantedEffects holds each support's compatible skill types and
  its skill-bar letter; GrantedEffectsPerLevel its cost multiplier.
- **Start:** the supports of each link group on their compatible skills, the support list in the skill record, cost
  multipliers, the higher-level-duplicate rule and the skill-bar letters. Then the effects the server's combat already
  models (added damage, more or less damage, cast and attack speed, critical strike, accuracy); every other support is
  listed in the docs as "linked, no effect yet". The rest of the combat effects are split with Qblolz on #64.
- **Done when:** Fireball linked with Arcane Surge shows the support's letter and pays the multiplied cost, a modelled
  support such as Faster Casting changes the skill, and the support crash is gone.

### Gems later: quality, getting gems, corruption, bonus levels
- **Quality** from Gemcutter's Prism needs currency on items (C→S 0x4a, "Items" above).
- **Getting gems:** quest rewards (QuestRewards, QuestRewardOffers: by class), the Act 1–4 vendors whose gems follow the
  character's level, class and quest progress, Siosa (Act 3) and Lilly Roth (Act 6 and later), and drops. Needs the
  quests and NPC shop items.
- **Corruption and Vaal skills:** Vaal Orb outcomes on gems (one level over the cap, quality up to 23%, the Vaal
  variant), the Lapidary Lens's double corruption, and Vaal souls for the Vaal skills. Goes with item corruption (#60).
- **Bonus levels:** "+1 to Level of Socketed Gems" and other item mods (#59), Empower Support and the awakened
  exceptional supports. Never past level 40, and they don't raise the gem's requirements.
- **Awakened supports:** their effects override their regular versions instead of stacking; their level requirements
  start at 72.

### Skill projectiles
- **Known:** Fireball casts, flies as a server-made projectile object and hits the targeted monster
  ([11](docs/protocol/11-skills.md) §9–10).
- **Start:** height grids for more areas (or in the walk-map recorder), collision with doodads and objects that have
  their own colliders.
- **Done when:** Fireball looks like the real one in every area: over the terrain, stopping at walls and objects.

### The game's start and resurrection
- **Known:** items, equipment, the skill bar, experience and passives are saved per character in `data/saves.db` ([saves](docs/saves.md))
  ([13](docs/protocol/13-items.md) §7, [11](docs/protocol/11-skills.md) §11, [15](docs/protocol/15-progression.md)), and
  every login starts at the last town's spawn point ([09](docs/protocol/09-area-change.md) §4). This follows the real
  game on purpose: the area and position a character was in are not saved, because the real client never logs anyone
  into them. New characters start on The Twilight Strand and log in there until they enter a town; Hillock's death
  (quest a1q1) opens the gate into Lioneye's Watch ([12](docs/protocol/12-quests.md) §6). The death
  screen's "Resurrect in Town" goes to the last town, with a loading screen, and is not offered before the character
  has one; "Resurrect at Checkpoint" goes next to the area's activated waypoint, else to the area's arrival point
  ([09](docs/protocol/09-area-change.md) §7).
- **Done when** (low priority: the start and the gate work, these are visual): the Strand's gate plays its opening when
  Hillock dies, the force wall blocks the way while he lives and the Doorman opens the door
  ([12](docs/protocol/12-quests.md) §6, "The gate's animation");
  "Resurrect at Checkpoint" goes to
  the last activated checkpoint of the area, with no loading screen. Waypoints are checkpoints, and the activated
  waypoint is done; the separate `Metadata/MiscellaneousObjects/Checkpoint` objects are not placed yet, so "last
  activated" across several checkpoints is open. Dying
  in a boss arena revives just outside the arena's door, even with a checkpoint activated; dying in a map revives in
  the hideout (hideouts exist, [21](docs/protocol/21-hideouts.md); maps do not).
- **One-way entries:** the arrival next to the exit back needs an exit back. Lioneye's Watch from The Twilight Strand
  uses the town's named point `entrance1` (`AreaLayout.OneWayArrivals`, [08](docs/protocol/08-area-transitions.md) §3):
  in the real game the player comes out on the town side of the gate. Other one-way links fall back to the area's spawn
  until their named entrance points are added.

## 4. Server design

- **Monster movement on the client's pathfinder:** monsters still use the fitted `WalkEstimator`. `ClientPathfinder`
  already paths them exactly as the client does; still missing are the client's monster controller (its own re-paths,
  frozen far monsters), the speed variance and the 5–50 ms until the client applies an order
  ([07](docs/protocol/07-latency-visibility-movement.md) §5, "Open").
- **Lockstep network mode:** the server would decide every position and the client play it back by server frames,
  which makes client and server identical by construction. Found so far: the frame protocol (S→C 0x2b6 / 0x2b5, already
  sent), the client's lockstep messages (0x018e, 0x018f, 0x0192, 0x0195, 0x0196) and the target update 0x1ad. Still
  needed: the client's action sequence (0x1ad is ignored without it), every action through the lockstep messages
  (NPCs, stashes, transitions, items, skills), and stepping every object by the server's frames
  ([07](docs/protocol/07-latency-visibility-movement.md) §6).
- **HNC packets from the client** (type 0xDD): what the server should do with them
  ([07](docs/protocol/07-latency-visibility-movement.md)).
- **Server-side world simulation** (monsters, combat): no project for it yet; add one when it is needed.
- **Several players** in one instance: the instance registry already supports it; the Gateway sends only the local player.

## 5. Project

- Rebuild the reverse-engineering setup from a fresh clone (Ghidra import, REA, Frida) and fix whatever the guide misses.
- Unnamed fields: [04](docs/protocol/04-player-entity.md), [05](docs/protocol/05-inventories-and-ui.md) and
  [09](docs/protocol/09-area-change.md) list fields whose meaning is unknown. Naming them is a good first task.

- Completed currency metacraft protection for supported explicit-affix currencies; specialized unsupported currencies and official Chance/Vaal outcome distributions remain separate work.
