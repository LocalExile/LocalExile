# Settlers of Kalguur: Kingsmarch

The Kingsmarch town is drawn by the client from its own room data, and the server supplies only the objects the client
refuses to build, the HUD panels, and the records those panels read (F-072, F-004, F-011). Two channels carry the
village: S→C `0x2c9` fills the settlement record the panels mirror (F-018, F-019), and C→S `0x2ca` carries every button
the player presses (F-051). The numbers a panel shows are the client's own arithmetic over a 245-byte state block and
the worker rows, so a number the server does not send is a number the panel renders as zero (F-044, F-060, F-063). The
fact layer behind every sentence here is [village-facts.md](../re/village-facts.md); that file holds the addresses,
file:line references and the probe evidence, and this one holds only what those facts mean for the server.

## Confidence levels

The markers are the same three the rest of `Agents/docs` uses, with the rule this pair of files adds: **every sentence
carries the fact ids it rests on**, and a sentence with an `unknown` marker instead is one the facts cannot support
(F-098).

- **confirmed** — seen in the running client or verified live; the fact entry says when and how (for example the
  recruitment panel and its prices on 2026-11-23, F-084).
- **candidate** — read off the client binary or off the server source, not yet seen at runtime (F-001).
- **unknown** — no evidence. Every such statement says *why* it is unknown, and the wording is "unreachable along the
  paths we read", never "nobody calls it" (F-098).
- **Addresses.** Code addresses and message ids hold for every allowed build; data addresses (globals, vtables,
  strings) do not, so nothing here builds a protocol on a data address (F-002).

## The town is client-side data, with one exception

- The room `Metadata/Terrain/Leagues/Settlers/HubTown/Rooms/kingsmarch.arm` (`room_point_rooms._row` 1984) lists 459
  doodad lines — the floor, the decks, the wharf, the buildings, the NPC lines, the farm plots and the props (F-072).
- 444 of those lines inherit `DecoUpgradeNoRender.ot`'s `BaseEvents { server_only = true }`, which is exactly the floor
  platforms, decks, wharf pieces, `DecoUpgradeLarge` buildings (tavern, blacksmith, mine hut), ships and farm plots
  (F-072).
- The doodad stage `0x142048e20` runs its placing steps in order and the room doodads are step 1, through the room loop
  `0x142046fc0` (F-064).
- A line becomes an object when its type is not serialised at all or is `client_only`; a `server_only` type is skipped
  (F-066). The type carries that flag from its own `.ot` (F-066).
- So those 444 lines are dropped by design, and the town shows its furniture floating over nothing (F-072, F-092).
  This is the one place where the client's own data is not enough and the server (or a patch) has to act (F-072).
- The server cannot restore them with a message: `0x2bb` resolves the type through the client's object-type register at
  `0x141674420` (F-070), and a key that is missing there logs "Unknown object type serialized by server" and creates
  nothing (F-070).
- Whether the HubTown types are absent from that register is **unknown** — the register's contents were not enumerated,
  so the claim "no HubTown path is in it" is not evidence, it is a hole (F-071).
- The workaround is a client-side patch, not protocol: `scripts/dev/kingsmarch-doodads.py` attaches with frida, hooks
  the room loop `0x142046fc0` (`:45`) and the per-line predicate `0x14152f9a0` (`:48`), and writes `type+0x2a8 = 1`
  (client_only) once per type, only while the room path is under the HubTown directory (F-069).
- Writing the flag also skips the invisible-store `or byte [obj+0x1e6],0x20` at `0x142048d59` (F-069), so the object is
  created visible rather than invisibly (F-069).
- The address `0x142047330` that the previous version of this note called "the decision", and the hook address
  `0x152f9a0`, are dropped: the predicate the script hooks and the repository's own notes name is `0x14152f9a0`
  (F-065, F-069, F-101).
- The count gate — "a line is created only when the type's component count is below 8" — is **unknown**: the
  comparison was not disassembled this session, so which interactive lines it keeps the patch off is unreachable along
  the paths we read (F-068). The component count itself is real and is set on the type before the client-side blocks
  are constructed (F-067).
- The previous version also recorded access violations in the client's `std::map` rebalance for a server-made copy of
  an interactive line (`FarmingPlotInteractable`, `NoticeboardInteractable`, `MapDeviceButton`, the calendar NPC lines),
  with RVAs `0x148938`, `0x148ad3`, `0x148b38`, `0x1ec7701`; that observation is carried as **unknown**, because it was
  not reproduced or re-read for this note (F-102) — it must not be read as "these objects are safe" (F-068).

## Objects the server does own

- **NPCs.** The town's NPCs are the `Metadata/NPC/League/Kalguur/` rows 3606–3668 of `NPCTalk` (F-079); `TownNpcs`
  copies the room line's cell and the room's origin (F-079).
- The eight cells the room lists are the King's Hand (429, 624), Tujen (283, 429), Rog (316, 523), Dannig (493, 478),
  Faustus (389, 625), Isla (213, 547), Sonja the Farmer (367, 794) and Raulf the Recruiter (422, 664) (F-080).
- `VillageGwennen` has no line in the room and is not placed (F-080); these eight are `candidate`, read by an earlier
  session and not re-read for this note (F-080).
- The NPC types list `ControlZone`, `Brackets` and `Functions`, whose client readers consume no bytes at all
  (`0x1400f1180`, the `ret` family), so they cost the object blob nothing (F-099).
- `VillageVisibilityController`'s `on_or_create_state_visible_0` disables targeting, highlighting and the minimap icon,
  and the state starts at 0, so the server sends `visible = 1` together with the "server value wins" flag `0x02`
  (F-100).
- **Farm plots.** `FarmingPlot` is a server object and its StateMachine's `plant` state is what the crop looks like
  (F-077).
- The server changes a crop by removing the plot (`0x2bd`) and creating it again (`0x2bb`) with a new plant state, one
  second after the removal (F-077).
- The id changes on every replant (F-077). Why a re-create under the old id would keep the old model is **unknown** —
  the client's per-state model selection was not read for this note (F-077).
- **Stash and guild stash** stand on the room's own cells (420, 611) and (420, 641) — **unknown**: those two cells come
  from an earlier reading, the room data was not re-read for them, and the town's object list carries only the farm
  plots and the boards (F-104, F-077).

## Opening the panels

- Every Kingsmarch panel is an HUD member window; the town map is `[hud+0xa00]`, 0x540 bytes, built by its constructor,
  and drawn by one Show routine (F-004, F-006).
- That Show refuses unless three pointers are non-null: `[win+0x2e0]` (the village rules container),
  `[win+0x300]+0x2f0` (the kind-0x9B record the widget found) and `[win+0x300]+0x2b8` (a `VillageJobs` row)
  (F-007).
- The container behind `[win+0x2e0]` is 760 bytes and is built by a factory from `[win+0x2D0]+0xB8` (F-009).
- On the town map `[win+0x2D0]` is the `ClientVillage` object, whose `+0xB8` was measured holding `0x38000001D0` — an
  integer, not a pointer — so the factory dereferences an unmapped address and faults (F-009). **confirmed** that this
  is the state of the release client: the value was read live (F-009).
- So the window cannot open on the town map until either the container is supplied another way or the factory's input
  is replaced; this is a client-side prerequisite, not something a message can fix (F-009, F-010).
- The widget looks the record up **once**, in its constructor, by scanning `village+0xCFD8` for kind `0x9b` (F-011).
- Therefore the record must exist before the area starts; the record list was measured empty while standing in town on
  2026-11-22, and a record created after that point is never bound (F-005, F-090).
- The records themselves are created by S→C `0x1f` through the client's factory table, and an existing kind is skipped
  rather than replaced (F-016). The town record is kind `0x9b`, 0x5A8 bytes (F-014).
- The field order of `0x1f` is **unknown** — two readings disagree on whether the kind is a `u32` or a `u8` — and the
  factories ignore the value, so either order creates the record until a capture settles it (F-094).
- Whether `0x1f` is the only creator of a record, or some other message also syncs one, is **unknown**: the id → handler
  table was not found for `0x1f`/`0x2c9`/`0x7b`, so this is unreachable along the paths we read (F-098).
- S→C `0x7b` is the panel-open message: a kind byte and four `u32` (F-020). The client's own screen table names `0x56`
  TownMap (F-020).
- Sending `0x7b` kind `0x56` drew the **world** map window, not the town map, live on 2026-11-20 (F-021). The mapping
  the server uses for its town-map option is therefore wrong on this build (F-020, F-021).
- `0x7b` kind `0x35` carries a `VillageJobs` row (F-020); what it opens is **unknown**, because no `0x35` frame was ever
  sent (F-020).

## Mining and smelting

- Mining consumes the `Pending*` counters (rows 0–4, the "Ore Marked" column) and fills the ore counters (5–9, "Ore
  Mined"); smelting consumes the ore and fills the bars (15–19, "Smelted Bars") (F-106).
- The twelve-miner and six-smith caps are `VillageBaseStats` rows 20713 and 20715, and the ore rank a crew may work
  is 20714 (mining) and 20716 (smelting); a job row's own `StatValue` is the rank it needs (F-106, F-013).
- One unit is `100 × BaseProductionTime` of the row's production counter, so the panel's "Time" column is that
  counter's remainder divided by the crew's efficiency (F-107).
- Each mined unit is worth `village_gold_per_ore_mined` gold in the treasury (F-106); the town's own base value for
  it is 0 until an upgrade raises it, so a fresh town earns nothing from ore.
- Mining and smelting run on the town's wall clock, driven by the area tick while a Settlers character is in the town,
  so the crews keep working while that character is in a map and for the gap a save leaves behind (bounded at four
  hours, `VillageProduction.MaxStep`). The panel is told once a second: the client re-simulates its own copy between
  pushes anyway (F-108), and a push per frame would be one state message every 17 ms.
- The town's treasury is one number: the harbour object's gold, the same purse the wages, the shipments and the
  recruitment draw on (F-081), so giving or taking treasury gold and hiring a recruit move the same field the panel
  shows.
- The town's ore, bars, work in progress, workers and upgrades are saved in `data/kingsmarch-village.json` (the same
  way the mapping line and the harbour keep theirs), so a relog or a restart does not empty the mine.
- **unknown**: the efficiency of a worker whose level the `VillageJobSkillLevels` table does not hold — the server
  pays it the lowest level's rate, the client's own fallback (`mov eax, ds:4`, 4 bytes) was not identified.

## The ore deposits and their encounters

- A deposit is only interactable when the **area load** places it (the client refuses the click on an identical node
  created later: it draws it, names it and passes its own state gate, but never sends C→S `0x24`, F-123/F-124). So
  deposits belong in `AreaGenerated`, never in a command.
- A map's deposit is claimed only after its encounter: Crimson Iron's Corrupted Growths and their Flesh Beyond, the
  Orichalcum altar's demons and its meteor herald, the Petrified Amber pack, the Verisium guards; Bismuth has none and
  pays on touch (F-114).
- The monsters are the client's own Settlers varieties, nine of them, all of which this server can spawn (F-113) —
  but spawning them crashed the client (an unhandled exception ~20 s into the Amber's fight, F-125). Until each variety
  is proven one run at a time (`Game_ORE_ENCOUNTERS=all`), a fight made only of unproven varieties does not start and
  its deposit pays on touch, as Bismuth's does.
- The amber is the client's punching-bag variety; its durability is the server's own percentage, falling while the pack
  is alive, and a broken amber pays nothing and spends the deposit (F-116).
- Each encounter's ore is the deposit's base amount scaled by the amber's remaining durability, and it is paid to every
  player in the instance (the wiki's "all party members will receive the resources", F-114).
- The Orichalcum fight picks its Beyond faction when it starts: Pale answers the altar (its Pale fodder and the
  skyfall herald), Demon answers with its own fodder and the thunder herald, and the faction that answered is the buff
  the fight leaves behind — K'Tash's Anger (2491) for Pale, Beidat's Wrath (2492) for Demon, 30 seconds, for every
  player in the instance (F-121, F-121b).
- **unknown**: the Petrified Amber's buff is not identified: no `BuffDefinitions` row carries its name, an amber id or
  an "infusion" id (F-115). Nor is Verisium's: the channelling shield 2718 needs the channel that is not modelled, and
  the page's post-channel "Verisium Infusion" has no row.
- The Verisium node's interaction is gated on two states the server owns: the client refuses to offer it while
  `encounter > 0`, and reads `begun_channelling` to decide whether the player is already channelling (F-119).
- A node's states are its type's own rows in `object_type_shared_states`: all five declare `activated`, `in_progress`
  and `ore_empty`, and each adds its own (`growths_alive`, `am_dead`, `encounter`/`type`/`lights`,
  `begun_channelling`/`channelling`/`in_zone`, `effect_off`) — F-122. The server sets the three shared ones and leaves
  the rest until a capture shows what they do.
- **unknown**: how the client reports a deposit channel — its object use is C→S `0x24` and a held action is C→S
  `0x197`/`0x198` with `0x199` ending it, but which of the two a Verisium deposit uses has no capture, and the
  interaction itself lives in the `.ot` data that `data/game-files.zip` does not carry (F-120). Until that is captured,
  the server starts the guards on touch and pays the base amount.

## The two channels

### S→C `0x2c9` — the state channel

- A frame is `u16 id` (the bytes `02 c9`) `| u8 kind | compact-uint length | payload`, and the payload begins with one
  sub-tag byte; the length is a single byte below 0x80, two below 0x4000 and three above (F-018).
- The reader `0x141FA7CA0` keeps the kind at `+0x90` and hands the payload to the record's vtable slot 8 (F-019).
- That handler compares the sub-tag byte against 0…13 in one chain and **never resynchronises**, so a single frame it
  cannot parse ends the connection (F-019). That is why every sub-tag below is sent only after its layout was read
  (F-019).
- The byte order of the `u16` id itself is **unknown** — one reading calls it big-endian, the server writes `02 c9` —
  and both agree on the bytes the server sends (F-096).

| sub-tag | payload | writes | level | fact |
|---|---|---|---|---|
| 0 | `u8 N`, then N worker rows | replaces the whole worker vector at `+0x160` (stride 0xB8) | candidate | F-038 |
| 1 | `u8 idx`, then the recruits | the recruit list at `+0x178` | candidate | F-039 |
| 2 | `u8 slot`, two interned names, 16 × u32 | one dock slot, `+0x2C0 + idx·0x60`, the u32 indexed by `VillageExports` | candidate | F-041 |
| 3 | `u8 idx`, 3 × u64 (each rotated left 56) | `+0x3E0 + idx·0x18` | candidate | F-043 |
| 4 | the 245-byte state block, then `u64 sentTime` | the counters `+0x190`…`+0x22F` and the clock | candidate | F-044 |
| 5 | one worker row | appends to the worker vector at `+0x160` | candidate | F-023 |
| 6 | `u8 idx` | erases the worker at that index | candidate | F-046 |
| 7 | `u8 idx`, `u8 nameKey` | selects the worker row | candidate | F-046 |
| 8 | `u8 v` | the selected dock slot (`+0x5A0`, `+0x5A1`) | candidate | F-046 |
| 9 / 0xA | `u8 idx`, `u8 count`, count × u32 | overlays the job context at `+0x2A0` with `VillageJobs`/`VillageJobTypes` rows | candidate | F-047 |
| 0xB / 0xC | three bytes | a hash map keyed by 3 bytes at `+0x4E0` | candidate | F-050 |
| 0xD | `u8 idx`, string | `+0x470 + idx·0x20`, only for idx 0…2 | candidate | F-050 |

- The previous version marked sub-tags 0, 1, 4 and 5 **confirmed** because it sent them and the panel responded; the
  fact layer records the layouts as `candidate` and only the panel result as confirmed (F-038, F-084), and this table
  follows the fact layer.
- The previous version read 9/0xA as the port quota from the port's six-number table (F-081); the handler's own reader
  overlays the job context (F-047), so 9/0xA is recorded under F-047 and the quota reading is not repeated.
- The **worker row** is 13 bytes with no job pairs: `u32 name key 1 | u32 name key 2 | u16 wage | u8 pairCount |
  pairCount × (u8 jobType, u8 skill)`, then the `VillageJobs` row index (F-042).
- The **recruit element** is that row plus a `u32` hire price (F-040).
- The recruit sub-tag reads its count byte **twice** — once in the handler, once in the reader — so the frame carries
  it twice (F-039); sending it once makes the reader run past the blob (F-039).
- The **state block** (sub-tag 4) is 245 bytes when the three blocks are empty: the 21 resource counters, the 15
  production counters, the gold, the ETA base, six flag bytes, three counter pairs, three small blocks, two spare
  values, the clock, a second `u64`, and finally the timestamp the client advances the town by (F-044).
- All of it is little-endian, and the clock must be whole Unix seconds: the handler converts the trailing `u64` from
  FILETIME, subtracts it and adds it to the record's own clock (F-045).
- If that leaves the record's clock ahead of now, the town advance logs id `0xFFE0ED8D`, "Attempting to advance village
  state to a time in the past", and the client faults at `0x140DEFC9A` (F-045). A zero, a millisecond value or a
  FILETIME in that field all do this.
- The frame's length was a real conflict: one reading counted 166 bytes, the other 245 (F-097). The 245 wins because
  the reader is line-by-line with load addresses and the server builds exactly that (F-097, F-044).
- **Sub-tag 9 takes the client down** (F-048). The layout is right; the crash is an embedded widget whose constructor
  looks an id up in a keyed table and dereferences the result even when the id is missing (F-049). The table that would
  key it is encrypted on disk (F-049), so this one is unreachable from the executable alone (F-049).
- The port panel's pin board `[ClientVillage+0x5C8]` exists only after the port window has been opened, which is why a
  port sub-tag sent early fails even with a correct payload (F-049).

### C→S `0x2ca` — the actions

- A frame is `u16 id | u8 kind | length | payload`, and the payload's first byte is the action, written by the
  serializer that built the frame rather than by the widget (F-051, F-053).
- The action layouts are all little-endian with no prefix byte: a tier (`0x00`), a row (`0x01`, `0x02`), an id and a
  row (`0x03`, `0x04`), an index (`0x05`), 67 bytes for a shipment (`0x06`), and a `u32` amount for the two gold
  actions (`0x18`, `0x19`) (F-052).
- The gold actions are 5 bytes total, and a captured one was `18 02 00 00 00` — amount 2 (F-057). A big-endian reading
  of the same five bytes would have made the amount 33554432 (F-057).
- The names the server gives the actions are `0x00` reroll, `0x01` hire, `0x02` release, `0x03` assign worker, `0x04`
  assign farm plot, `0x05` upgrade, `0x06` shipment, `0x07` abandon, `0x0a` abandon captured, `0x0b` reroll port quota,
  `0x12` pause mapping, `0x13`–`0x16` take reward, `0x18`/`0x19` gold (F-056).
- The previous version's short list (0x00/0x01 = reroll+hire, 0x02 release, 0x03 assign, 0x18/0x19 gold, 0x04 assign
  farm, 0x05 upgrade) agrees on the actions it names but omits the shipment, abandon and reward actions the code has
  (F-056).

## Where the panel's numbers come from

- The panel does not read the record directly: it reads a 0x110-byte snapshot, and that snapshot runs a mini
  simulation first (F-058, F-062).
- With no elapsed time the simulation leaves the copy untouched (F-062), which is why a state block sent before the
  area starts shows the values as sent (F-058).
- The snapshot is 21 resource counters, 15 production counters, the gold, the ETA base, the six flags and the counter
  blocks, in the order the state block uses (F-059).
- The treasury gold on the panel is the record's `+0x198` (F-026). The `ClientVillage+0xC40C` field the previous version
  cited is **unknown** — it is carried as F-103 and was not re-read for this note (F-103).
- The total wage line is computed by the client from the worker rows and the 15 production rows, with nothing added
  numerically by the panel itself (F-060).
- With no worker rows every term is 0, so the wage is 0 and the line renders empty rather than "0" (F-063).
- That supersedes the previous explanation ("the client sums workers by a production row's job key, and the key is not
  in the table we have"): an empty line follows from zero wage, which needs no missing key (F-060, F-063).
- The ETA is `(+0x198 · 3600 − +0x19C) / W` (F-061), so it too is empty while the wage is zero (F-063).
- The counters arrived as zero in the live session of 2026-11-23 (F-087), which is what a state block whose counters
  are all zero produces (F-044, F-028).
- The server now writes them: the 21 resource counters are the town's own ore stock (rows 0–4 marked, 5–9 mined,
  15–19 bars) and the 15 production counters are the work each `VillageProduction` row has done on the unit it is
  making (F-107, F-106).
- One production row is one line — a `VillageJobs` row's crew turns an `Input` resource into the row's `Output` in
  `BaseProductionTime` seconds (F-106) — and the line's rate is the client's own formula, `3600 × Σefficiency /
  BaseProductionTime`, where a worker's efficiency is the skill-level row's second column, 18 at rank 1 and 63 at
  rank 5: the community's 1×, 1.5×, 2.05×, 2.72× and 3.5× curve (F-109, F-110, F-111).
- A crew whose designated ore has nothing marked works another ore the town holds instead, which is the wiki's own
  wording for both lines (F-112).

## The dialogue that gets you into the panels

- The town's dialogue rows are 3606–3668 and the village options are rows 3610–3613 with a fallback at 3556 (F-078).
- "Manage Town" is row 3610 and needs league flag 334 in the *second* flag set; without it the client never lists the
  row (F-088).
- Live on 2026-11-20 the row was absent until the probe showed the flag test, and appeared after `0x02bc` bit 3 with
  334 was sent; the click then sent `0x82` option 4 and the server answered `0x7b` (F-088).
- The other rows need flags 1 (Fund the Treasury), 1434 (Invest in Recruitment), 95 (Town Recruitment) — and the later
  variant row 3611 needs 1436 and explicitly refuses 334 (F-078).
- This is the only part of the village where a wrong server answer is silent: a missing flag produces a shorter
  dialogue, not an error (F-088).

## What the server implements

- `Modules/Core/Village/VillageModule.cs` holds the panels' channel: the settlement (treasury, workers and their ranks,
  recruits), the `0x2c9` frames for the worker list, the recruit list and the state block, and the `0x2ca` action
  handling (F-073, F-074, F-076).
- `Modules/Core/Village/VillageProduction.cs` runs the mining and smelting rows and writes the counters they produce;
  `Modules/Core/Village/KingsmarchOre.cs` puts claimed ore deposits into the town's "Ore Marked" counters (F-106–F-112).
- Its constants are the record kinds (`0x9b` town, `0x74` farm) and `RecruitPrice = 2000` with a
  `VillageRecruitBalancePerTier` step of 2000 a tier (F-073, F-076).
- The settlement's capacity is 90, from `VillageBaseStats` row 20728 (F-073).
- `Modules/Core/Village/VillageWorkers.cs` and `VillageWages.cs` carry the worker row layout and the five wage ranks,
  with `Skill = clamp(rank - 1, 0, 4)` (F-082).
- `Modules/Core/Game/World/KingsmarchTown.cs` holds the town's server objects: the `kingsmarch-town.json` list, the
  object limit, the room origin (138, 138), the farm plots, the noticeboards, the records and the `0x7b` frames
  (F-077).
- It replants a plot by removing it and re-creating it one second later (F-077), and it sends the six farm flags and the
  counters the state block carries (F-030, F-044).
- `Modules/Core/Game/World/KingsmarchPort.cs` holds the harbour: the port rows, the six-number quota, the gold and the
  port list (F-081).
- `data/kingsmarch-town.json` is generated from `client.db`'s `room_doodads` room 1984 — 444 rows of
  `{type, model, x, y, angle}` plus the origin — and is not committed (F-083).
- The dev files `data/kingsmarch-frames.txt`, `kingsmarch-state.txt`, `kingsmarch-recruits.txt` and
  `kingsmarch-open-port.txt` let a frame be tried against a live client without a rebuild (F-075).
- The commands in the previous version of this note (`/village`, `/village hire`, `/village assign`, `/recruit`,
  `/treasury`, `/port …`, `/unlock`) do **not** match the module's own usage string, which names
  `/villagepanel hire|gold|purse` (F-076). Which aliases exist is **unknown** — the command registration was not
  re-read for this note (F-076).
- `UnlockAll.cs` and `data/unlock-all.txt` (the campaign gate flags) are **unknown** here — they come from the previous
  version, and the file was not re-read for this note (F-105).

## Open questions

- **Where do `0x1f`, `0x2c9` and `0x7b` dispatch?** The candidate table is not the one used, and no other was found, so
  a bulk record sync on some other id cannot be ruled out — unreachable along the paths we read (F-022, F-098).
- **What is record `0x74`?** The server calls it the farm record; one reading calls it the harbour/expedition record.
  Its constructor also needs `m_JobLevelCaps` (id `0x86A`), and its draw routine indexes a vector with no null check,
  so an empty `0x74` kills the client about 11 s later — unknown which of the two it is (F-015, F-093).
- **What does `0x7b` kind `0x35` open?** It carries a `VillageJobs` row and no frame was ever sent (F-020).
- **What is the component-count gate, and is the patch safe on interactive lines?** Not disassembled this session
  (F-068).
- **What are the contents of the object-type register?** Not enumerated, so "HubTown types cannot be created" is a
  hole, not a result (F-071).
- **What is the recruit-vector stride?** 0xB0 in one reading, 0x10 in another (F-095).
- **Which byte order is the `0x2c9` id?** (F-096).
- **What is `0x1f`'s field order?** (F-094).
- **What fills the five `InstanceDetails` maps?** No insert site was found for any of their five keys, and the object is
  created in one login path only — unknown whether anything ever fills them (F-035).
- **The visibility and no-op readers** (`0x1400f1180`) are candidate: no live probe watched a component reader consume
  zero bytes (F-099, F-100).

## COVERAGE STATEMENT (judgement layer)

This note cites only ids from [village-facts.md](../re/village-facts.md); that file carries the examined and
not-examined lists in full. Repeated here so a reader of this file alone knows the boundary.

**Examined** — client functions: the town map Show and its window and widget (`0x140529450`, `0x1405235c0`,
`0x140521d00`), the row fill (`0x140523520`), the panel reader and snapshot (`0x140529970`, `0x1417690F0`), the time
advance (`0x1424C4550`), the two record handlers (`0x141767F60`, `0x14168A080`), the `0x2c9` reader and the `0x2ca`
constructor and sender (`0x141FA7CA0`, `0x141FA7A10`, `0x1421A1960`), the wage model and the worker-row readers
(`0x1424C32E0`, `0x1424C7F80`, `0x1424C7CA0`, `0x1424C9080`, `0x1417737D0`), the doodad stage and its filter, placement
and predicate (`0x142048e20`, `0x142046fc0`, `0x142035630`, `0x142035a10`, `0x14152f9a0`), the object-type ctor and
register (`0x141ec7c10`, `0x141674420`, `0x141ec8170`), the container ctor and factory (`0x1418cc180`, `0x140363210`),
`ClientVillage` and its slot 8 (`0x1415A65D0`, `0x1415a6800`), `InstanceDetails` and its only creator (`0x1416563B0`,
`0x140D57090`), and the no-op reader `0x1400f1180` (F-001–F-105).

**Examined** — message ids: `0x1f`, `0x2c9` (sub-tags 0–0xD), `0x2ca` (actions `0x00`–`0x07`, `0x0A`–`0x1D`), `0x7b`,
`0x82`, `0x84`, `0x2bb`, `0x2bc`, `0x2bd`, `0x2aa`, `0x85` (F-018–F-022, F-038–F-052, F-088).

**Examined** — the production half: `0x1424c4550`, `0x1424c5250`, `0x1424c32e0`, `0x1424c2e20`, `0x1424c2af0`,
`0x141769b20`, `0x1424c5b80`, `0x1424c3fb0`, `0x1424c5870`, `0x1424c6440`, `0x1424c8190`, and the tables
`VillageProduction`, `VillageJobSkillLevels` (F-106–F-112).

**Examined** — tables: `VillageJobs`, `VillageResources`, `VillageProduction`, `VillageJobTypes`,
`VillageFarmAdjacency`, `VillageBaseStats`, `VillageUpgrades`, `VillageShippingPorts`, `room_doodads` (room 1984),
`room_point_rooms`, `NPCTalk` (rows 3556, 3606–3668), `NPCs` (F-013, F-028–F-030, F-073, F-078–F-083).

**Examined** — runtime probes: the read-only frida scripts and live sessions of 2026-11-20 (dialogue row and `0x7b`),
2026-11-21 (record creation), 2026-11-22 (record list, counting probe, sub-tag 9) and 2026-11-23 (panel numbers,
reroll, hire, treasury) (F-005, F-021, F-084–F-092).

**Not examined — separately, and never to be read as "no problem".**

- Client functions: the real wire-id dispatcher (if it is not the candidate table), the per-id handler entry for
  `0x1f`/`0x2c9`/`0x7b`, the comparison against the component count (F-068), the reader that builds the port panel's
  pin board `[ClientVillage+0x5C8]`, the encrypted keyed table at `supplier+0x168`, the two base vtables `+0x18`/`+0x158`
  (F-037), and every draw path of the `0x74` record beyond the null-check hazard.
- Fields never read by a path we read: `+0x150`, `+0x2A8`, the `+0x3E0` triples, `+0x4D0` (F-036).
- Message ids: `0x1e` (bulk record sync), `0x2c8`, `0x2cb`, `0x7c`, `0x7d`, and every other instance id the server does
  not send for Kingsmarch. `0x1e` matters most: if it carries record contents, the "only `0x2c9` mutates the record"
  statement above would need re-checking (F-098).
- `0x2ca` actions `0x08` and `0x09` (not found in the family), `0x1e`, `0x1f`, `0x1a` (F-052).
- Tables: `VillageJobLevelCaps`/`m_JobLevelCaps` (id `0x86A`) contents, the client's
  `VillageRecruitBalancePerTier` rows, `VillageShippingFailures`, the encrypted keyed table (F-015, F-049, F-081).
- The object-type register's actual contents (F-071) — not enumerated.
- The doodad component-count branch (F-068) — not disassembled this session.
- Addresses and numbers carried from the previous version of this note and not re-read: `0x142047330`, `0x152f9a0`, the
  four rebalance RVAs `0x148938`/`0x148ad3`/`0x148b38`/`0x1ec7701`, `ClientVillage+0xC40C`, the two stash cells
  (420, 611)/(420, 641), and `UnlockAll.cs`/`data/unlock-all.txt` (F-101–F-105).
- Runtime probes not run this session: no client was started, no frame was re-sent, and no live session was repeated
  for this document.