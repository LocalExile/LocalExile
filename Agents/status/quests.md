# Status: Quests

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/quests.md](../roadmap/quests.md).

## Works (verified with the real client)

- Quests: a1q1: new characters
  start on The Twilight Strand's beach and log in there until they reach a town;
  coming within 60 cells of Hillock sets `a1q1-SeenHillock` ("Kill Hillock");
  Hillock has the boss life bar at the top of the screen once he aggroes (StateMachine `boss_life_bar`, 0x2ba), unique
  life (311 at level 1) and a second stage: at half life he pulls out his sword (ZombieBossExtract, Transitionable → 2)
  and his life fills up again; the force wall (`Tutorial_Blocker_3`, the room's angle and scale 2.7) stands across the
  way to the gate until he dies, and the server closes the way on its own walk map too (`QuestBarriers`), so its
  position estimate stops where the client stops; after his death the quest log says to enter the gate (every
  character has `CompletedAllTutorials`); his death levels the character to 2, removes the wall and creates the gate
  into Lioneye's Watch (the Gateway checks the flag too); Portal Scrolls do nothing on the Strand (its area mod
  MapNoPortals); no Resurrect in Town before a town.
  Code: `Act1.cs`, `BossFights.cs`, `QuestBarriers` (QuestObjects module), `AreaModCatalog.cs`.
  Docs: [12](../docs/protocol/12-quests.md) §5–6.
- a1q4 "Breaking Some Eggs" in The Mud Flats (played through live, seed 1): the three Rhoa Nests stand at the captured
  tile records and each drops its glyph once per instance while the character still needs it.
  Picking a glyph up sets `a1q4-KnowsAboutGlyphs` and `a1q4-HaveGlyphN`; coming near the Glyph Wall sets
  `a1q4-SeenGlyphWall`.
  Using the wall with all three glyphs takes them, sets `a1q4-PlacedGlyphs` and plays the wall's transition (0x2ba to
  Transitionable).
  The client's invisible wall then opens, and the exit into The Submerged Passage appears; until then the server's
  walk estimate stops at the same invisible wall as the client.
  Progress is in the save: after a server restart the wall stays done and the passage open.
  Tarkleigh's "Glyph Reward" offers the six skill gems and sets `a1q4-QuestRewardTaken`.
  Needs the area's `-Walkmaps` capture (`1_1_3_seed<seed>.objects.json`).
  Code: `GlyphQuest.cs`, the QuestObjects module, `Act1.cs`.
  Docs: [12](../docs/protocol/12-quests.md) §1–4.
- a1q5 "Mercy Mission" (played through live, The Tidal Island seed 1): Nessa gives the quest and Bestel his clue
  through their own NPCTalk rows, and the quest tracker follows the flags.
  Hailrake stands at the shipwreck's `mapboss` (the medicine_boat room's captured placement) and drops the Medicine
  Chest, unless Nessa has it, the character carries one or one lies on the instance's ground.
  `a1q5-HaveMedicineChest` follows the carried chest; Nessa's "The Medicine Chest" takes it with
  `a1q5-NessaCongratulated` in one save, and a chest found after that is taken away.
  Needs the area's `-Walkmaps` captures (seed 1 has them): without them Hailrake is not placed.
  Her two rewards give a normal flask (item level 4) and a support gem of the class, once each.
  Progress survives relogs and server restarts.
  Hailrake's drop is a rule of the engine's table, the chest's flags and hand-in its `.it` Quest component's.
  Code: `MercyMission.cs`, `Act1.cs`, `QuestItems.cs`, `QuestRewardCatalog.cs`.
  Docs: [12](../docs/protocol/12-quests.md) §7.
## Partly working

- The quest engine and all of Act 1 on it, checked by the Act 1 oracle and not yet played with the client on the
  modules (a1q1, a1q4 and a1q5 under "Works" were played before they moved onto it; the other Act 1 quests below were
  played on the engine before the game moved into modules): flags per character in the save, one declarative table per
  quest, one file per act (`Modules/Core/World/QuestObjects/Acts/Act1.cs` …, registered by the QuestObjects module and
  run only for the leagues that list it; [campaign quests](../docs/game/campaign-quests.md) "Adding a quest"),
  `/quest_flag`.
  Kills, sightings, room objects come near, exits, cleared areas, quest chests, room NPCs, NPCs that turn into
  monsters, second forms and mirrored blockers are rules; quest items (their `.it` Quest flags: held, picked up,
  handed in, removed, used), dialogue flags and scripts, session flags and the rewards come from the client's data.
  Quest passive points (QuestStaticRewards) are counted from the flags into the tree's `extra_skill_points` and stay
  after a relog; refund points are credited once (the Book of Regrets: 54 → 56); QuestStaticRewards' stats are stat
  sources. `QuestSimulator` plays an act's events through the engine; `QuestEngineTests` checks Acts 1–6 against
  `Act1Complete`, `Act2CompleteEramir`, `Act3Complete`, `Act4Complete` and every Act 5 and 6 quest's "complete" row,
  and Acts 7–10 and the Epilogue after them against `E3EndOfAct8` and `EpilogueAtlasDemo`.
  What existing saves see: a character with `a10q3-SeenBoss` but without `EndGameAvailable` earned no longer has the
  Act 10 −30% resistance penalty (it follows `EndGameAvailable`, QuestStaticRewards 39); a second copy of a quest book
  (from `/item`) is used up without its points once the first was used; a quest item lying on the ground after its
  quest is done with it (a Medicine Chest after Nessa has one) goes when clicked instead of being picked up.
  Code: Gateway `Quest/` (`Quests.cs`, `QuestItems.cs`, `QuestStaticRewards.cs`, `QuestStates.cs`, `QuestSimulator.cs`),
  Game `World/Quest/`, the QuestObjects module.
  Docs: [12](../docs/protocol/12-quests.md) §5, [campaign quests](../docs/game/campaign-quests.md).
- a1q8 "A Dirty Job" and a1q7 "The Dweller of the Deep":
  Kadavrus and the Dweller spawn at `mapboss`; seeing and killing them set their flags; the last of The Fetid Pool's 96
  monsters sets `a1q8-Cleared`. Tarkleigh's rows, then his book rewards (offers 5, 6, which opened nothing before) are
  claimed; using the Book of Regrets gives two refund points, the Book of Skill a passive point (`extra_skill_points` 1).
  Docs: [12](../docs/protocol/12-quests.md) §8.
- a1q2 "The Caged Brute": Brutus spawns in the Warden's
  section (`mapboss2`); Piety's room sets `SeenPietyPrison`; his death creates the exit to Prisoner's Gate, entered;
  Tarkleigh's reward is claimed.
  Docs: [12](../docs/protocol/12-quests.md) §9.
- a1q6 "The Marooned Mariner": Fairgraves stands on his
  ship; "Marooned" gives the quest; the slave girl's chest in The Ship Graveyard Cave drops the Allflame; "Thank You"
  hands it in and turns him into the boss, who fights; his death, Bestel's book and its passive point follow.
  Docs: [12](../docs/protocol/12-quests.md) §10.
- a1q3 "The Siren's Cadence" and Act 2: Merveil, the Siren
  spawns in her arena, her death brings Merveil, the Twisted, whose death sets `a1q3-KilledSeaWitch` and creates the
  exit; the character entered The Southern Forest. Nessa's help and reward are claimed.
  Docs: [12](../docs/protocol/12-quests.md) §9.
- a1q9 "The Way Forward", Act 1: Piety's blockage at Prisoner's
  Gate sets `a1q9-SeenBlockage` and `a1q9-RoadBlocked`; the road to The Western Forest stays closed (Act 2 opens it).
  After all of Act 1 the save had every `Act1Complete` flag.
  Docs: [12](../docs/protocol/12-quests.md) §9.
- Acts 2 and 3 (live 2026-10-30, seed 1, on the engine before the modules: the campaign runner on a character started
  with `/quest_preset Act1Complete`; on the modules checked by the oracle and the offline exit audit, not yet with the
  client): a2q11 (Arteri, his emblem, the Thaumetic Seal, Bestel's book), a2q10, a2q6 (Helena climbing up after
  Fidelitas, the Strange Device), a2q1–a2q3 (the bandit lords killed, their amulets), a2q4, a2q5 (the crypt's altar,
  Yeena's book), a2q7 (Eramir takes the amulets: a passive point; The Apex), a2q9 (the tree poisoned with the spike and
  the gem), a2q8 (the Ancient Seal, the Dark Altar, the Vaal Oversoul, the pyramid's exit), a3q1–a3q5, a3q8, a3q9,
  a3q11–a3q13 and a3q10 (the Epic Door, Dominus' two forms, Lady Dialla on the tower, the exit to The Aqueduct) ran
  unattended in one pass and ended in Highgate with every flag of `Act2CompleteEramir` and `Act3Complete` and every
  Act 2/3 quest log at "Quest Complete".
  Multi-item hand-ins take every item in one click (Eramir's amulets, Hargan's busts, Siosa's pages, Fairgraves' plum
  and decanter).
  The Library's candle opens the hidden door for the instance only.
  The two exits the layout disables and a quest gates (The Upper Sceptre of God → The Aqueduct, The Library → The
  Archives) are created (`scripts/re/exits --quests`: `gated` at seed 1 and three other seeds).
  Every quest object, chest and NPC the rules name is placed at seed 1.
  Driver step files: `scripts/dev/campaign/quests/a2q*.py`, `a3q*.py`.
  Code: `Acts/Act2.cs`, `Acts/Act3.cs` (QuestObjects module).
  Docs: [12](../docs/protocol/12-quests.md) §11–12; [act 2](../docs/game/campaign/act2.md),
  [act 3](../docs/game/campaign/act3.md).
- Acts 4, 5 and 6 (live 2026-10-30, seed 1, on the engine before the modules: a fresh Witch from `/quest_preset
  Act3Complete`, the campaign runner unattended; on the modules checked by the oracle, not yet with the client): a4q2
  (Voll, Deshret's Banner, the seal opens the Mines), a4q6 (Deshret's spirit, the book), a4q3 and a4q4 (Dialla at the
  Rapture, Kaom, Daresso, both Eyes handed in), a4q5 (the Rapture Device, Piety in the Belly and her talk, the three
  guardians' organs in one click, the Black Core), a4q1 (Malachai's second form, the withered Dialla opening the Ascent,
  her reward), a5q1a (the Resonator), a5q1b, a5q3, a5q2, a5q4 (the Templar Courts door, Avarius → Innocence, Sin's talk,
  burnt Oriath's exits to their own versions of the areas), a5q5 (Bannon, Utula), a5q7 (the relic cases, one click) and
  a5q6 (the Ossuary tomb, the Sign in its socket wakes Kitava, `/kill` reaches his threshold: `a5q6-CompletedBoss`,
  −30 %; Lilly sails to Lioneye's Watch), then a6q4 (The Twilight Strand cleared), a6q5, a6q3 (Tukohama), a6q2
  (Shavronne), a6q7 (Abberath, both phases), a6q6 (Ryslatha) and a6q1 (the three Nessas, the Beacon lit, the Black
  Flag, the Brine King; Weylam sails to The Bridge Encampment). It ended in 2_7_town with every flag of `Act4Complete`
  and the act 5/6 completion flags; every area change through an exit, a waypoint or an NPC-script transport. The god
  Sins appear where the gods die. `a4q1-KillGuardians` is not set; the Beacon's fuel carts are left out (the lever
  lights it). Driver step files `scripts/dev/campaign/quests/a4q*.py`, `a5q*.py`, `a6q*.py`.
  Code: `Acts/Act4.cs`, `Acts/Act5.cs`, `Acts/Act6.cs` (QuestObjects module), Highgate's two Diallas in `NPC/TownNpcs.cs`.
  Docs: [12](../docs/protocol/12-quests.md) §13–15; [act 4](../docs/game/campaign/act4.md),
  [act 5](../docs/game/campaign/act5.md), [act 6](../docs/game/campaign/act6.md).
- The Pantheon (live 2026-10-30 before the game moved into modules: the campaign runner chose The Brine King and
  Tukohama; on the modules checked by the oracle and the data test, not yet with the client): a god unlocks with its
  boss's kill flag (Act 6's four, the later acts' as their tables come); C→S 0x1ee chooses the major or minor god of a
  PantheonPanelLayout row in a town, a hideout, the Rogue Harbour or after Sin's god row (`CanChangePantheon`), saved
  per character and shown by the Player component (creation blob and update 0x0080); the god's power (and the souls'
  upgrades once `EndGameAvailable` is earned: every character is given it at login, so it counts only after a quest
  set it) joins the stat sources. `/pantheon` and /state's `pantheon` for the driver.
  The panel's highlight of the choice is not checked; the souls (Divine Vessels) are not built.
  Code: the Pantheon module (`Modules/Core/Player/Pantheon`), Gateway `Player/PantheonCatalog.cs`, Core
  `InstancePantheon.cs`. Docs: [pantheon](../docs/game/campaign/pantheon.md).
- Acts 7–10 and the Epilogue (live 2026-10-31, seed 1, on the engine before the modules: one Witch from
  `/quest_preset E3StartOfAct7`, the runner unattended per act, no `/goto`; on the modules checked by the oracle and
  the offline exit audit, not yet with the client): a7q5, a7q2 (Maligaro's Map, the Reverie Device's portal into the
  Sanctum, Black Death's venom), a7q3 (Silk's Obsidian Key, the Sins barrier, the passage into The Den), a7q1
  (Greust), a7q8, a7q9, a7q7 (seven scattered Fireflies, Yeena burns the webs), a7q6, a7q4 (Arakaali, the bridge into
  Act 8); a8q1, a8q5, a8q4, a8q6, a8q7 (the legion's multi-kill), a8q3 and a8q2 (both orbs at the fight starter,
  Solaris and Lunaris, the bridge exit into Act 9); a9q3 (the mummy event), a9q5 (the Bottled Storm carried to the
  barrier, both Shakaris), a9q4, a9q2 (the feather to Irasha), a9q1 (both ingredients in one click, the membrane, the
  three spirits, the Trinity, Lilly's voyage); a10q1 (Plaguewing comes near Bannon), a10q6, a10q4, a10q5, a10q2
  (Bannon becomes Innocence: Oriath Docks' two stages), a10q3 (the horns, Kitava's scripted end: `EndGameAvailable`
  earned, −60% with Act 5's, before him −30%; Sin's portal home) and A11q0 (Lilly sails to Karui Shores). Every act
  7–10 quest done with every act 7–10 flag of `EpilogueAtlasDemo`, 24 quest passive points and 20 refund points.
  The exits the layouts disable and a quest gates (The Ashen Fields' way north, The Harbour Bridge's into Act 9, Sin's
  portal home) are created (`scripts/re/exits --quests`: `gated` at seed 1 and three other seeds); burnt Oriath's
  exits keep Act 10's destinations where Act 5's rules name Act 5's (`AreaTransitionCatalog.SameAct`). Before Kitava
  the Epilogue stays shut (`EndGameAvailable` is sent only in a hideout and the maps until earned:
  `Quests.EndgameOnly`). Every quest object, chest and NPC the rules name is placed at seed 1 but The Crossroads'
  BanditBlock (its sight is optional).
  Driver step files `scripts/dev/campaign/quests/a7q*.py` … `a10q*.py`, `A11q0.py`, `a10q3a11.py`.
  Code: `Acts/Act7.cs` … `Acts/Act10.cs` (QuestObjects module), Oriath Docks' InnocenceTown line in `NPC/TownNpcs.cs`.
  Docs: [12](../docs/protocol/12-quests.md) §16–19; [act 7](../docs/game/campaign/act7.md) …
  [act 10](../docs/game/campaign/act10.md).

- New characters pick up their class weapon, talk to the dying exile and kill the biting zombie for their skill gem.
  The support gem comes from its chest, which requires the player to walk into range.
  Placements use captured room data and gem claims are saved once; existing characters keep their items and progress.
  The complete Witch weapon, zombie/gem and chest/support-gem sequence was checked live.
  Tutorial popup acknowledgements and force walls 1/2 remain unimplemented, and placement still needs local captures.
  See [opening encounter](../docs/protocol/12-quests.md#opening-encounter-2026-10-25).
- Quest exits with a Transitionable (Merveil's exit, the arena doors) open without their animation (plain
  AreaTransition), and the arena doors are not created at all.
- The bandit vote (a2q7's help path: talking to a bandit lord opens the client's vote panel through
  `DispatchRulesetEvent(BanditCallVote)`, VoteType/VoteState) is not implemented; its messages are not
  reverse-engineered.
  The lords are hostile and the choice comes from the kills (the campaign runner kills all three); the help rewards'
  stats (QuestStaticRewards 4–6) apply once their flags are set.
- Characters whose activated waypoints carry a quest flag as an access flag (a1q1's Hillock for the Coast) skip that
  flag's world changes: with OfflineWitch, The Twilight Strand's wall was not created and its gate stood open.

## Not working yet

Nothing yet.
