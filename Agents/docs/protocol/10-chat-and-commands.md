# 10 — Chat input, server replies and dev commands

Status (live, 2026-10-16): everything typed in the chat box reaches the Gateway. Slash commands run on the server and
answer with chat lines (S→C 0x17, one per line of the reply; message boxes until 2026-10-21). `/set_speed` works since 2026-10-17: the client reads the new `movement_velocity_+%` and
walks faster. The first version sent the wrong key (see §3). `/spawn_mob` works since 2026-10-17: it puts a visible,
idle monster next to the player (§5).

## 1. C→S 0x15: chat input

| Field | Encoding |
|---|---|
| text | compact wide string (`LoginWriter.WStr`: u16 count, per code point 1 byte < 0x80, 0x80 + UTF-16LE, 0xC0 + u32) |
| u8 | 0 in every capture |

What arrives is the text as typed: slash commands as is (`/spawn_mob 42`), plain chat with the channel prefix the client
added (`#hello server` for global). The client handles none of our commands locally, but the words of Commands.dat
rows with `SendsGenericCommandPacket` never arrive as chat: the client sends C→S 0x16 with the row + 1 instead
(`/destroy` 9, `/hideout` 45: [21](21-hideouts.md) §3), so our commands must not reuse those words. `InstanceChat.Length` frames it
(the length depends on the text), and `SplitClientMessages` uses it for id 0x15.

## 2. Server → client text

Candidates that read a wide string were found by mapping every S→C factory case to its class and read function (slot
2) and keeping those that call the wstr reader `0x140e0cbe0`. Each was then sent live with `/raw`:

| Id | Layout | What the client did |
|---|---|---|
| **0x0d** | wstr | **Message box with the text and an OK button.** Class `0x1435bf650`, dispatcher case 0 → listeners at +0xa1f0. Used for replies. |
| 0x12d | u16 code, wstr | Shows `BackendErrors.dat` row `code - 1` (1 → "An account with this name already exists."). The UI listener `0x140be6ef0` prints the row text as is and uses the wstr only to compare a name, so `{0}` is not filled in. |
| 0x26 | u16, wstr | Disconnect with a reason (handler `0x142149ca0` closes the socket). Do not send it. |
| 0x157 | wstr, u8 | "System challenges you to PvP" popup (duel request). |
| 0x10 | u32, wstr | Nothing visible (no dispatcher case). |
| 0x188 | wstr, u16 | Nothing visible. |
| 0xa9 | party info | Not chat. |
| 0xc9 | u8, wstr, wstr | Friend presence map. Not chat. |

**S→C 0x17: chat line** (class `0x1435dcec0`, read `0x140e0c710`, dispatcher case 2 → listeners at instance +0xb78 →
chat panel `0x140179f20` / `0x14017a300`; verified 2026-10-20 with `/raw`): wstr sender (its first character picks the
channel), wstr text, wstr guild tag, u16 flags (raw; bit 8 sends the message to the +0xd78 listeners instead, bit 3 adds a
field read by `0x1408402a0`, bit 7 sets a mode 5), u8, u8, 8 bytes, u8, u8 n + n × (u32, item) linked items. An empty
sender with every other field 0 prints the text alone (`InstanceChat.ChatLine`). The client's `<green>{text}` markup
in the text does not colour the line (live 2026-10-21). But with an empty sender, the text up to the first `: ` is drawn
green like a local speaker's name and the rest white (live 2026-10-21: `/com accuracy` was green up to `default:`); the
`/commands` header ends with `:` for that. Sender prefixes (live 2026-10-21, `/raw`): `#Test` shows `#Test: line` in
the global colour (red), `$Test` `$Test: line` in trade orange, `@Test` `@From Test: line` in whisper purple, the whole
line coloured; `%` and `&` show nothing without a party or guild. A sender of the prefix alone (`$`, `#`) prints the
text white with no name, like an empty sender. So a coloured line always shows its sender and a colon. With an empty sender, `0x14017a300` wraps the text in a colour
looked up by `0x142426ea0(byte & 0x1f)` (the byte at +0x158 of the object at instance +0xc810) and runs it through the
markup parser `0x14018fb50`; `ChatColours.dat` has rows Default, Red, Green, Blue, ChayulaMessage. Per-line colour: open.

Previously: the chat line message was not found. It does not go over the login connection
(Auth received nothing after Play). The client also prints "Timeout while joining global chat channel 820", so the
channel join is a request we don't answer yet.

## 3. S→C 0x1b3: stats update

World group; class `0x1435dda98`, read `0x141fe1350`. The world handler (`0x142187480`, case 0x1b3) looks up the
object's Stats component and calls `0x1424cfaf0(stats, key, value)` for each pair.

```
u16 0x1b3, u32 object id, u32 0, u16 0,
compact uint n, n × (compact uint stat key, compact int value)
```

- Unsigned compact (`0x14089cad0`): < 0x80 is 1 byte; then 0x80 + 14 bits, 0xc0 + 21 bits, 0xe0 + 28 bits; else 0xf0 +
  u32. Signed compact is `0x141fa3d60` (`InstanceProtocol.CompactInt`).
- The stat key is the client's own stat id: the int its getter `0x1424cfa40(stats, key)` binary-searches in the sorted
  (key, value) vector at stats+0xf0, which `0x1424cfaf0` fills. With the vendored schema it is **the Stats.dat row + 1**
  (key 0xf5 = row 244 `movement_velocity_+%`; checked on every key an attack reads, [11](11-skills.md) §8). `StatKeys`
  in the Gateway lists the ones in use.
- Walk speed (`0x141e7c0b0`): base (`+0x548`) × (1 + key **0xf5** / 100, at least −100). With a flag it is also
  multiplied by (1 + key 0xce6 / 100). It is capped by key **0x44f** (`movement_velocity_cap`) when that is positive.
  Measured live: sending 0xf5 = 200 makes the getter return 200 and the walk visibly faster.
- The Stats component has no 0x2ba (component update) handler: its vtable slot +0xa0 is a no-op. So 0x1b3 is the way to
  change stats.

## 4. Commands (Gateway, `Program.Commands.cs` `Commands`)

| Command | Effect |
|---|---|
| `/commands [page \| name]`, `/com …` | lists the commands, 8 per page (`/com 2`, `/com 3`, …; after four blank lines a green header, then each command's syntax only), or the full usage of one command (`/com item`; otherwise every command whose name contains the text) |
| `/raw <hex>` | sends the bytes as one S→C message (dev probe; spaces allowed) |
| `/set_speed <percent>` | 0x1b3 `movement_velocity_+%` = percent − 100 on the player; the server's walk estimate uses 37 × percent / 100 cells/s |
| `/set_stat <stat name or client key> <value>` | records the stat for the player, sends the player's stats (0x1b3) with the attack's recomputed final stats, and the skill again (0x2ba, [11](11-skills.md) §4). Names are Stats.dat Ids (key = row + 1). An attribute (`strength`, `dexterity`, `intelligence`) also sets the `virtual_*_for_requirements` value: the client's gear and gem requirement checks read those, not the attributes ([13](13-items.md) §2d) |
| `/spawn_mob <row, Id or name>[@level] [friendly] [normal\|magic\|rare\|unique] [name]` | 0x20 + 0x2b9 + 0x1a9 for a MonsterVarieties row at a level (default 1), hostile unless `friendly`, with a rarity and an optional name, a few cells from the player (§5) |
| `/set_level <1 to 100>` | sets the character's level in the account store (Auth control op `set_level`) and sends it in place: the level's experience to the Player component (0x2ba, [04](04-player-entity.md) §4.6; the client derives the level from it), the level's stats (0x1b3: level, life, mana, accuracy, …, [11](11-skills.md) §4), the skills again (0x2ba Actor) and full life (0x1b1) |
| `/heal` | full life and mana for the player (0x1b1); from 0 it also revives |
| `/add_xp <amount>` | experience as from a kill: levels up, saved ([15](15-progression.md) §1) |
| `/passives` | the passive and refund skill points and the allocated passives |
| `/add_passive_points <n>` | adds n extra passive skill points (the 0x33 `extra_skill_points` field), saved with the character; sends the tree (0x33) |
| `/add_refund_points <n>` | dev shortcut: adds n refund points (0x33's first u16), saved with the character; sends the tree (0x33). Orbs of Regret also add them (right-click) |
| `/gold <amount>` | adds gold, or takes it with a negative amount (not below 0); the account's gold (every character of the account shares it), saved and sent as S→C 0x2a9 ([04](04-player-entity.md)); every area entry sends the saved amount too |
| `/add_passive <PassiveSkillGraphId>` | allocates one passive node through the same checks as C→S 0x34, saved and sent back (0x33) |
| `/reset_passives` | refunds every passive, saved, and sends the tree (0x33) |
| `/resistances` | the character's uncapped and capped fire/cold/lightning/chaos resistance |
| `/hit <fire\|cold\|lightning\|chaos\|physical> <damage>` | dev: damages the player through the resistance model (physical through armour), showing the mitigation used and the life left |
| `/accuracy [monster level]` | final accuracy rating and attack hit chance against a normal and an evasive monster of that level (default: your level; spells always hit) |
| `/defences [monster level]` | armour, physical reduction and evasion with the chance to evade a monster of that level |
| `/auras` | active auras, their flat/percent reservation, reserved and unreserved mana, reservation efficiency and aura effect ([11](11-skills.md) 13) |
| `/suppression` | Spell Suppression chance and damage reduction |
| `/spellhit <fire\|cold\|lightning\|chaos\|physical> <damage>` | dev: a spell hit through resistance and Spell Suppression, rolling the suppression chance |
| `/crit` | attack and spell critical strike chance and multiplier from the unconditional passives and weapon base |
| `/block` | attack block and spell block chance, caps and effective lucky chances |
| `/block_luck <attack\|spell\|both\|off>` | dev: force lucky block for testing |
| `/blockhit <attack\|spell> <fire\|cold\|lightning\|chaos\|physical> <damage>` | dev: a hit through block and resistance |
| `/god` | toggles 1000% increased attack and cast speed, 100% of life and 10 000 mana regenerated per second, +10 000 life, +1000 to each attribute ([11](11-skills.md) §12) |
| `/death_screen [hex flags]` | S→C 0x55 with these flags (default: the league's; 2 hides it), [11](11-skills.md) §7 |
| `/quest_flag [<QuestFlags Id> [off]]` | lists the character's quest flags, or sets or clears one (saved); the quest's room objects follow ([12](12-quests.md) §5) |
| `/goto <area id or name>` | change area as if through an area transition (e.g. `/goto 1_1_3`; ids from the atlas), [08](08-area-transitions.md) §7 |
| `/transition [pos … \| rot <deg> \| z <z \| none> \| model <path \| none> \| index <n>]` | show or change the first area transition (re-created), [08](08-area-transitions.md) §5 |
| `/mobs` | lists the monsters spawned on this connection |
| `/despawn [all \| <id>]` | S→C 0x2bb (remove object, header only) for the last spawned monster, all of them, or one id |
| `/mob_walk [0x<id>] [skill hex]` | 0x1ab: the monster starts its Move action (skill 0x2909) toward the player; it walks, animated, along the client's path |
| `/mob_move [0x<id>] [cells/s]` | 0x1ba: slides the monster in a straight line (probe; no animation or pathing) |
| `/mob_tp [0x<id>]` | 0x1b7: teleports the monster next to the player |
| `/npc <row, Id or name> [rotation°]`, `/npcs`, `/pos` | creates an NPC next to the player ([17](17-npcs.md)), lists the NPCs, prints the player's cell |
| `/ai [on\|off]` | monster AI for this connection (on by default): monsters chase the player (below) |
| `/cosmetic portal <name, number or part of a name>`, `/cosmetic list [filter]`, `/cosmetic off` | the shop's portal effects, as game data ([21](21-hideouts.md) §5). Several effects containing what was typed are listed with their numbers rather than the first being picked |

Every player can use every command; there are no permissions yet.

**No tab completion for our commands.** The chat prompt sends only the finished line (C→S 0x15, §1) and completes
nothing itself — its slash commands are the client's own Commands.dat words, which the server cannot add to (the 71
rows, and the only ones the client sends as C→S 0x16). A command that takes a game-data name therefore has to accept a
piece of it: `/cosmetic` does, and answers an ambiguous piece with the matching effects and their numbers, so the next
line can be a number instead of a name.

## 5. Monsters (`/spawn_mob`)

**Live 2026-10-17:** `/spawn_mob ZombieBite@13` three times put three Hungry Corpses next to the player, standing
idle (first with an "Unidentified" tag, gone once the identified bit was set). `check-components.js` traced all three: 11 components read in the order below,
198 bytes consumed, 0 left. Code: `MonsterObject`, `InstanceEntities.MonsterTypes` (`InstanceEntities.cs`),
`MonsterCatalog.cs` and the `spawn_mob` command in the Gateway. Tests: `InstanceEntitiesTests.Minimal_monster_blob_layout`,
`Monster_types_message_and_type_path`.

Sent, in order: **0x20** (once per variety and level on a connection), **0x2b9**, **0x1a9**.

- **Type.** The object-type register ([04](04-player-entity.md) §1) files every row of the 0x31a-byte table, which is
  **MonsterVarieties.dat** (10 691 rows × 0x31a in 3.29.3b), under the row's `Id` with `BaseMonsterTypeIndex` (row
  +0x48) as its .ot, e.g. `Metadata/Monsters/Zombies/ZombieBite` (Hungry Corpse) on
  `Metadata/Monsters/Zombies/Drowned/BiteZombie`. The variety Ids have no .ot of their own.
- **Level: S→C 0x20.** A monster type needs a level. The Monster component's factory (`0x141e6fd00`) throws
  **"Creating monster with no level"** when its type's `+0x20` is 0. The Monster type (vtable `0x1435a5a28`, parser
  slot 9 `0x141e701d0`) parses the number after the last **`@`** of the type path into `+0x20` (1..100; otherwise
  "Unable to parse monster level of …" / "Unable to create a monster type at level …"). The register files the plain Id
  only; the server registers the levelled path with **0x20** (instance group, dispatcher case 9, class `0x1435dc320`,
  read `0x1421a4f30`):
  ```
  u16 0x0020, u16 n (BE), u16 preload (BE, +0x10), n × u16 MonsterVarieties row (raw, little-endian), n × u8 level
  ```
  Per pair the handler looks the row up (`0x142288c90`, a row index into Data/MonsterVarieties.dat) and registers
  `"<Id>@<level>"` extending the plain Id (`0x141ec2540`; level 0 → the plain Id through `0x141ec28e0`), appending the
  type to instance `+0xc828`; then it loads the first `preload` new types (`0x140685030`). The 0x2b9 type is then
  `ObjectTypeHash("<Id>@<level>")`.
  - Without 0x20 (the first live try) the exception is thrown while the components are built (`0x142427510`, before
    the blob is read), and the cleanup then frees a Targetable component that was never created: an access violation
    in its deleter `0x14161d160`. So the client hangs and crashes instead of showing a message.
- **Components.** `Metadata/Monsters/Monster.ot`: Positioned, Stats, ObjectMagicProperties, BaseEvents, Pathfinding,
  Buffs, Life, Animated, Inventories, Actor, Monster ([../re/object-types.md](../re/object-types.md)). Every one that
  reads is already known from the player or the exit; Monster (`0x1435a6778`), BaseEvents and Pathfinding read nothing.
  - Positioned: 16 bytes. Its 8-byte prefix (`param_1[0x2b] + 0x21`) is written into the object's **Sector** component,
    so it is for types with one; the .ot keys (`0x141e2d860`) set +0x14..+0x20, +0x22, +0x23 but not +0x21.
    `serialise_collidable` (Monster.ot) sets +0x1b.
  - Actor: the two conditional u8 are read when `components[type+0xbc] + 0x1a8 < 1`. +0x1a8 is the Life component's
    first pool `+0x178 + 0x30`, the **current life** just read from the blob, so a living monster has 17 bytes.
  - ObjectMagicProperties (base part `0x142201d70`): u16 flags, u8 `+0x7a`, then the component's own u8 flags.
    Flags bits 0–2 = **rarity** (`+0x7c`: 0 normal, 1 magic, 2 rare, 3 unique); bit 3 = **identified** (`+0x78`;
    without it the client tags the monster "Unidentified"). Only when identified: bit 7 (0x80) → u32 UTF-16 unit count +
    units, a **custom name** (`+0x18`, shown instead of the variety name); else bit 8 → u8 n + n × u32 keys (name parts,
    looked up by `0x14225a4d0`, not mapped); then bit 5 → mod list. Before them: bit 9 → 2 × u8, bits 0x10/0x40/0x400/0x800 →
    u8 n + n mod records (`0x142430380`). Live 2026-10-17: flags 0x08/0x09/0x0a/0x0b showed normal, magic, rare and
    unique monsters; 0x8b with "Headhunt" showed a unique named Headhunt (ObjectMagicProperties 24 bytes, 0 left).
  - Life: (current, 0, …) for life; mana and energy shield 0. The **maximum is the client's own**: its Monster type
    (`0x141e701d0`) sets stat 0x7d (and other default stats: 0x142–0x147, 0x75, 0x78, 0x87, …) from DefaultMonsterStats
    row `level − 1` (0x48-byte rows) and the variety: life = max(1, column × LifeMultiplier / 100), the column being
    AltLife1 (`+0x2c`) if the MonsterTypes row has AltLife1, else AltLife2 (`+0x38`), else MinionLife (`+0x1c`) for
    IsPlayerMinion, else MonsterLife (`+0x14`). The Gateway computes the same (`MonsterCatalog.BaseLife`) and sends it as
    the current life. Live 2026-10-17: Hungry Corpse at levels 13 (77 life) and 70 showed full life bars; the level-70 one
    also showed a cold icon (probably the resistance profile of its MonsterTypes row growing with level; not checked).
  - Blob: 112 bytes with the base list, plus the Animated path (198 bytes for Hungry Corpse).
- **Other component lists.** Of the 10 691 varieties, 4 513 have exactly the base list; most others append
  DiesAfterTime, StateMachine or Transitionable. The blob also writes MinimapIcon (u8), DiesAfterTime (no data: its
  live vtable is the factory's `0x14359f478`, slot 17 `ret`), Preload, Functions, PlayerClass (no data), Transitionable
  and StateMachine (below): **10 345 varieties** are supported; the other 346 have NPC, ControlZone, BlightTower,
  Mercenary, LimitedLifespan … components and are refused with a message box. The Gateway knows each type's list and
  data from `data/object-types.json` (`python -I scripts/re/ot_components.py --all --json data/object-types.json`,
  about 2 minutes); without it, it assumes the base list and refuses StateMachine types.
- **Transitionable** (`ClientInternalTransitionable` `0x1435dd290`, slot 17 `0x1421c24b0`): u8 = state (low 7 bits,
  `+0x120`) | bit 7 (`+0x122`, transition in progress). States run **1..num_states** (.ot `Transitionable { num_states
  = N }`, default 1, the type's `+0xb0`); `num_states` = fully transitioned (`create_transitioned`), lower =
  untransitioned (`create_untransitioned`). The post-create slot `0x1421c26f0` always runs the event at index
  `(state − 1)` of a 0x18-byte array, so **state 0 is invalid**: it read a garbage script pointer and crashed in the
  animation-event interpreter (`0x141ded7b9`, live 2026-10-17, Alira and Gravel Eater). The Gateway sends
  `num_states` (extracted by `ot_components.py` as `transitionable.num_states`).
  - "Emerge" monsters (Gravel Eater, Disbanded Dead …) are created and stay hidden or lie as a corpse: their .ot pauses
    the `emerge` animation at creation and only their `Emerge` action (`on_transition`) shows them. That needs the
    server to start the action.
- **Animated path.** Not sent (flags 0): the type supplies the animated object. `Game_MONSTER_AO=1` sends the variety's
  first `AOFiles` path instead (bit 0), which also works for plain monsters but is not needed.
- **StateMachine** (live vtable `ClientInternalStateMachine` `0x1435da8b8`, slot 17 `0x142191f10` → base
  `0x141e3ee40`):
  ```
  u8 shared-state count, u8 shared-timer count       (the client throws unless both match the type)
  per shared state, in definition order:  compact int value (0x141e490e0), u8 flags (0x02 keeps the sent value)
  per shared timer:                       u32, u8
  ```
  Only states declared with `define_shared_state` and timers with `define_shared_timer` in the type's .ot chain
  (top-level StateMachine sections, parents first; a string is a `;` list of `name` or `name = default`; a
  redefinition keeps the first position and takes the later default) are networked; `define_state` / `define_timer`
  stay on the client. `ot_components.py` extracts them (`shared_states`, `shared_timers`); the Gateway sends the
  defaults. Live 2026-10-17: Frost Bishop (1 state, 4 bytes), Arakaali (4 states, 10 bytes) and Haku (8 states, one
  defaulting to 1, and 2 timers: 28 bytes) all consumed exactly. Arakaali showed no hover nameplate: bosses carry
  `boss_life_bar` / `disable_life_bar` / `global_first_aggro` states, probably the top-of-screen boss bar on aggro.
- **Removal.** S→C **0x2bb** (world group, header only: `u16 id, u32 object id, u32 0, u16 0`) removes the object;
  live 2026-10-17 `/despawn` and `/despawn all` removed spawned monsters.
- **Movement** (live 2026-10-17). **S→C 0x1ab "action started"** on the monster, with the Move skill **0x2909**
  (the same id as the player's Move: a basic action every Actor has, Monster.ot `basic_action = "Move"`), sequence
  0xffff (not client-predicted), flags 0x0408, the monster's position and the target: the monster **walks with its
  animation, along the client's own path around walls, on the terrain height**. Alternatives: **0x1ba** (Positioned
  move) only slides it in a straight line through walls, at `duration = cell distance / value` (value ≈ cells per
  second; `0x141e30c90` → `0x141e34120`), keeping the old height; **0x1b7** teleports it.
- **AI** (Gateway, `RunMonsterAi`; live 2026-10-17). Every 500 ms per connection: a monster within its MonsterVarieties
  `MaxAgroRange` (taken as grid cells: 60 for Hungry Corpse, 105 for most) of the server's estimate of the player starts
  chasing: a Move action (0x1ab, 0x2909) to the cell `ObjectSize + 1` from the player, re-sent when that cell moves by
  3+ cells; beyond twice the range it gives up (a Move to where it stands). The server follows each monster's walk with
  a `WalkEstimator` (A* on the walk map) at MonsterVarieties `MovementSpeed` cells/s (8 for Hungry Corpse, 44 for Haku)
  and reports that position as the action's start; live, the chase showed no snapping, so the estimate keeps up. All
  sends go through one lock per connection (the AI tick and the read loop share the Salsa20 stream).
- **Team** (live 2026-10-17). Positioned's u16 at `+0x1e0` (after x, y and the f32 at `+0x29c`) is the object's team;
  the .ot `team` values are only defaults. The player is sent team 0. Monsters sent with 0 had a **green** (friendly)
  hover outline; with 1 the outline is **red** and on The Coast a life bar shows over the head (towns hide hostile life
  bars). The Gateway sends 1, or 0 with `/spawn_mob … friendly` (both verified live: red and green outlines).
- **Combat** (live 2026-10-17, [11](11-skills.md)). The player has the default attack (skill 0x0102); a click on a
  monster sends C→S 0x24 with it, which is answered with 0x1ab aimed at it (flags | 0x80 + u32 target id after the flags, write
  `0x141fdd990`), then **S→C 0x1b1** lowers its life: `header, u32 current, u32 reserved, u8 pool (0 life, 1 mana,
  2 ES), u16 (+0x2a), u8 (+0x2c)`, big-endian (read `0x141fe0790`); the world handler (case 0x1b1) applies it to the
  pool (`0x141e69fb0`) and calls the Life component's death slot (vtable +0xe8) when life goes from ≥ 1 to < 1, revive
  (+0x100) the other way. Each hit takes the unarmed damage (2–5 for the Witch) at the swing's contact point, after the
  player has walked into range; the timing is in [11](11-skills.md) §5. A dead monster's life bar is hidden
  (`hide_mini_life_bar`) and the monster is removed (0x2bb) 10 s later. Monsters hit back with
  their own skill ([11](11-skills.md) §6).
- **Placement.** The nearest walkable cell 6–20 cells from the server's estimate of the player; ids from 0x3000. The
  world is active, so 0x2b9 is created at once; 0x1a9 puts it in the wake zone.

## 6. Open

- The chat line's flags and channels, and the channel join request and its answer.
- Monsters: **skills** (their GrantedEffects, so they fight back), mods (the
  ObjectMagicProperties mod lists; rarity life bonuses come from mods), the boss life bar (StateMachine `boss_life_bar`), the Emerge action for emerge monsters, the 346
  varieties with other components (NPC, ControlZone, …), AI and combat.
