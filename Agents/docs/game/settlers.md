# Settlers of Kalguur: Kingsmarch

What the 3.29.3c client does with the Settlers hub (`KalguuranSettlersLeague`, HASH16 `0x8f53`), and what the server has
to send because the client will not build it itself. The league's village UI is **live in this build**: the panels are
opened by a server message, and the server now fills them (workers, recruits, the treasury). The hub is still driven by
server-owned objects for the parts the client cannot build.

Claims below are marked **confirmed** (seen in the client), **candidate** (read off the binary, not seen) or **unknown**;
each *confirmed* claim was checked against the running client on 2026-11-22/23.

## The town is client-side data, with one exception

`data/tables/room_doodads` row set for the room `Metadata/Terrain/Leagues/Settlers/HubTown/Rooms/kingsmarch.arm`
(`room_point_rooms._row` 1984) lists 459 lines: the floor, the decks, the wharf, the buildings, the NPC lines, the craft
plots and the props. The client builds them itself **except** the 444 lines whose type inherits
`DecoUpgradeNoRender.ot`'s `BaseEvents { server_only = true }`: those are the floor platforms, decks, wharf pieces,
`DecoUpgradeLarge` buildings (tavern, blacksmith, mine hut), the ships and the farm plots. Dropped, the town shows its
furniture floating in the air over nothing.

The drop happens in the doodad stage `0x142048e20` → room-line loop `0x142046fc0` → decision `0x142047330`; the reason is
a field of the **type record**, not of the line: `[record+0x80]` is the number of components the server serialises for
that type (written by the `.ot` parser at `0x14247b649`), and the client creates a line only when that count is below 8.
`type+0x2a8` (written once by the type ctor `0x1ec7c10` from `record+0xef`/`+0xf0`) says server-owned, and creating a
line while it is 2 leaves the object invisible (`0x142048d59 or byte [rdi+0x1e6],0x20`, read by `0x141e33ac2`).

No server message can supply those objects: `0x2bb` looks the type up in the client's `Metadata/GameObjectRegister`
(`0x141674420`) and throws `Unknown object type serialized by server` for a HubTown type — the register is filled from
eight dat tables (`0x141ec8170`) and no HubTown path is among them. So the town's floor comes from a **client-side
patch**, not from the protocol: `scripts/dev/kingsmarch-doodads.py` attaches to the running client with frida, hooks
`0x2046fc0` to learn the room path and `0x152f9a0` (the decision's vtable slot `+0x98`) to keep the line, writing
`type+0x2a8 = 1` once per type and only while the room is under `Metadata/Terrain/Leagues/Settlers/HubTown/`. The count
gate (`[record+0x80] < 8`) keeps it off the interactive lines (`FarmingPlotInteractable`, `NoticeboardInteractable`,
`MapDeviceButton`, the calendar NPC lines): those are `read 8` types, the client builds them from its own room data, and
a server-created copy of one of them corrupts the client's object map (access violation in its `std::map` rebalance,
RVAs `0x148938`, `0x148ad3`, `0x148b38`, `0x1ec7701`).

## Objects the server does own

- **NPCs.** `TownNpcs`' Kingsmarch room (`kingsmarch.arm`: the .dgr's only node, at the area's origin with rotation I,
  so the room's cells are the area's cells) places the eight villagers the room lists on their own line's cell, with
  its angle and its model: the King's Hand (429, 624), Tujen (283, 429), Rog (316, 523), Dannig (493, 478), Faustus
  (389, 625), Isla (213, 547), Sonja the Farmer (367, 794) and Raulf the Recruiter (422, 664). The client's own type
  table gives the farmer Isla's model, so the room's model is the one sent; `VillageGwennen` has no line in the room and
  is not placed. Their types list `ControlZone`, `Brackets` and `Functions`, which deserialize
  through the same no-op reader as `BaseEvents` (`0x1400f1180`, `ret 0`), so they cost the blob nothing.
  `VillageVisibilityController`'s `on_or_create_state_visible_0` runs `DisableTargetable()`, `DisableHighlighting()` and
  `HideMinimapIcon()`, and `visible` starts at 0, so the server sends `visible = 1` with the "keep the sent value" flag.
- **Farm plots.** `FarmingPlot` (`read 6`) is a server object: its StateMachine's `plant` state selects the crop model, and
  the model swap lives in the client's `on_or_create_state_plant_<n>` events, so a crop change is a remove (`0x2bd`) and
  a create (`0x2bb`) with a **new id** — re-creating the same id leaves the old model. A plot is never removed twice
  within a few seconds. `num_workers` is the same StateMachine.
- **Stash and guild stash** stand on the room's own cells (420, 611) and (420, 641).

## The village UI opens, and the server fills it

- The panel is the client's own window: the HUD creates it at init (`0x140dad910`) — the town map is `[hud+0xa00]`
  (0x540 bytes, ctor `0x1405235c0`, Show `0x140529450`) — and the game's "Toggle Town Map" button opens it. The server
  opens the same window: S→C `0x7b` with kind `0x56` shows it (live 2026-11-23, after the records arrived before the
  area start; the same kind sent while the records were missing showed nothing).
- Show has three gates and needs all of them: `[win+0x2e0]` (the village rules container), `[win+0x300]+0x2f0` (the
  settlement record it drew) and `[win+0x300]+0x2b8` (the job row). The client reads the village's records **once**,
  in the town map widget's constructor (`0x140521d00`, called from the window's ctor at `0x140527b1c`): it scans
  `village+0xcfd8` for a record of kind `0x9b` and, if the list is empty, binds nothing and the panel stays shut.
  So the record has to be in the village **before the area starts**.
- The records are built by S→C `0x1f` (a kind byte and a value; the client makes one record per kind from its own
  factory table, kind `0x9b` is the town record, 0x5a8 bytes) and the client skips a kind it already has. The server
  sends them with the area's own messages (`GameEvents.BeforeEnterMessages`), before `0x21`; sending them from the tick
  is about 300 ms too late and the panel never opens (live 2026-11-22).
- `ClientVillage` is created by the client in the area-load function (`0x140d57380`, 0xe208 bytes, vtable `0x14346cc18`),
  and **no message writes its own state**: the treasury gold field `+0xc40c` is only ever zeroed by its constructor
  (`0x14214dada`), the other 43 references read it. The settlement record the server builds is a shell; what fills it
  is the village channel `0x2c9` (see below), whose sub-tags write the record's worker vector (`+0x160`), its recruit
  list (`+0x178`) and the panel's counters (`+0x190`..`+0x22f`).
  S→C `0x2c9` is stored as a list of handles (reader `0x141fa7ca0`) and
  is not in the instance dispatcher's table at all; the village action channel C→S `0x2ca` is what the panels send
  (`0x00`/`0x01` reroll and hire, `0x02` release, `0x03` assign, `0x18`/`0x19` give/take treasury gold, `0x04` assign
  farm, `0x05` upgrade), which the server answers.
- **confirmed**: with the record, the workers, the recruits and the treasury sent, the panels show them — the
  recruitment panel lists the workers with their job, level and wage, the candidate cards carry their price, the hire
  takes the price out of the treasury (and then out of the player's own gold when the treasury is short), the give and
  take buttons move the treasury against the player's stash, and a click on a plot moves a worker between it, the mine,
  the smithy and the idle column (live 2026-11-23/24). The state frame is what carries the numbers, so its layout
  matters:
  - sub-tag `4`, 166 bytes are read (the reader stops at `0xa6`) but the declared length has to cover 245 bytes or the
    client raises "Buffer Underflow", so the unused tail stays zero. `0x00`..`0x54` are 21 `u32`, one per
    `VillageResources` row (the store: crops 10..14, ores 5..9, bars 15..19); `0x54`..`0x90` are 15 `u32`, one per
    `VillageProduction` row (Mining1..5, Smelting1..5, Farming1..5); then the treasury `u32`, then a `u32` at `+0x198`
    whose meaning is unknown — the wage total written there crashed the client (fault `0x162869f`, inside the shipping
    failure roll), so it stays 0; `0x98`..`0x9e` are six bytes, the `VillageResources` row of each farm plot (10..14 for
    a crop, `0xff` for a bare plot — row 0 has no name object and the label resolver `0x1404f9e60` faults at `+0x8`, so
    a zero there kills the client); the last eight bytes are the frame time in seconds.
  - the panel's total wage line is the client's own addition of the worker rows' wage field, which is why the server
    charges the treasury that same sum (`VillageWages`), not a flat rate.
- **unknown**: the map icons' site counters (`Mine n/12`, `Farm n/6`, `Forge n/6`) are filled from the client's stat
  cache (`0x142502d90`, rows 20714/20716/20719/20729), not from the state frame, so they show the base numbers a
  settlement with no upgrades has rather than the workers standing there.

## What the server implements

- `Modules/Core/Village/VillageModule.cs`: the village panels' own channel — the settlement (the treasury, the workers
  and their ranks, the recruits), `0x2c9` frames for the worker list, the recruit list and the counters, the `0x2ca`
  actions (reroll, hire, release, assign, give and take treasury gold) and the wage table the worker rows carry
  (`Modules/Core/Village/VillageWages.cs`, `Modules/Core/Village/VillageWorkers.cs`).
- `Modules/Core/Game/World/KingsmarchTown.cs`: the town's server objects (farm plots, then optionally the noticeboards),
  crop growth per plot, workers, wages, the treasury, and a click on a plot to plant or harvest. The farm runs on one
  clock, the wall clock in Unix seconds (`UtcNow()`), the same base the plots' catch-up and the harbour's trips use, so a
  town saved by one process is still at the right point in the next. An empty treasury halts the town: the plots stop
  growing (`PlotRate` returns 0) and the mine and the smeltery stop bringing anything up -their cycles are moved to the
  tick that notices the halt, so the hours it stood idle are not paid out as produce once it is funded again.
  The save (`data/kingsmarch-farm.json`) names each plot's object id, so a module reload -which starts a fresh record
  while the client still holds the town's objects- picks the plots back up under the ids the client knows (`Adopt`)
  instead of sending new ones into a map that already has them. Only placement is opt-in per module load (`_armed`);
  the farm, the mine and the smeltery keep running after a reload. A plot whose crop is being changed stays in the
  record under its old id with `Plot.Replanting` set until the new object exists, so a report or a save written in that
  second is never missing a plot.
- `Modules/Core/Game/World/KingsmarchPort.cs`: the harbour — the seven ports of `VillageShippingPorts`, cargo value from
  `VillageExports`, crew, travel time and risk from distance, and the failure rows of `VillageShippingFailures`. The town
  has three dock slots (`Docks = 3`): a slot is bound for a port (`Route`), cargo is loaded into it out of the farm's
  store (`Load`), and `Sail` turns it into a `Ship` at sea that ties up its crew, banks its barter when it is due and
  gives the slot back; `Unload` puts a dock's cargo back in the store. The clock is the wall clock in Unix seconds
  (`Clock()`), so a trip saved before a restart is still due after it.
- Commands: `/village` (report), `/village hire`, `/village assign <worker> <plot>`, `/village crop <plot> <crop>`,
  `/recruit`, `/treasury`,
  `/port` (report), `/port route <slot> <port>`, `/port load <slot> <crop> <count>`, `/port sail <slot> [port]`,
  `/port unload <slot>`, `/port send <port> <crop> <count>`, `/port crew <n>`, `/port storm <percent>` (dev), `/unlock`.
- `Modules/Core/Game/World/UnlockAll.cs`: grants the NPCTalk gate flags a completed campaign would have set (see
  [progression](../../status/progression.md)); `data/unlock-all.txt` makes the safe set automatic.

## Data

`data/kingsmarch-town.json` is generated by `scripts/dev/kingsmarch_town.py` from `client.db`'s `room_doodads`
(room 1984): 444 rows of `{type, model, x, y, angle}` plus the room origin (138, 138). It is the input the town's
spawner walks; it is not committed, `scripts/dev/extract-data.ps1` writes it.

`data/doodad-collision/KalguuranSettlersLeague.json` is generated by `scripts/dev/kingsmarch_collision.py`: the cells
those rows (and the hub's eight villagers) would block, as `[y, x0, x1)` runs in the area's grid. The shapes are the
`.ao` `Hull` walk triangles and circles the client itself stamps into its walk grid (`0x141d8ad70`), turned by each
row's angle; a row whose `.ao` has no `Hull` but whose type is blocking with an `object_size` gets that square.

## Walking: the client walks, the server clips

The client computes every step, so nothing the server marks as blocked stops it: `SetBlocker` is answered for skill
targeting but a click still walks through, and so does the server's own walk map. What does work is refusing the
destination — `Modules/Core/Game/Player/Messages.cs` clips a `0x25` click against the stamped grid
(`KingsmarchTown.Slide`, trying the straight line and then ±30/55/80°) and the client follows the clipped cell. Two
things learned the hard way: the ring of cells around a blocked one must stay walkable (the grid reads a cell as
walkable at value 2 and above, so a distance falloff pins the player between crates), and a hull thinner than a cell has
to be sampled at nine points, not just the cell's centre, or a lamp post or bench falls between the samples.

## The village state channel (S→C `0x2c9`)

Every number a Kingsmarch panel draws comes from `0x2c9`: a frame is
`u16 0x2c9 | u8 kind | compact-int length | blob`, where `kind` is the ruleset record kind (`0x9b` = the town record) and
the blob is one sub-tag byte plus its payload. `ClientVillage` hands the blob to its vtable slot 8 (`0x141767f60`), which
compares that byte against 0…13 in a chain (`0→1→2→3→4→5→6→7→8→9→10→11→12→13`) and **never resynchronises**: one frame it
cannot parse ends the connection. The server had no `0x2c9` sender at all before, which is why every panel opened with
the client's own defaults (population 0/90, no quotas).

The sub-tags, with the offsets they write. **confirmed** = sent and seen in the client, **candidate** = read off the
binary (full table in `Tools/re/tmp/kmre/KALGUUR_SHIPPING_FRAMEWORK.md` §4):

| sub-tag | payload | writes | |
|---|---|---|---|
| 0 | `u8 count`, then `count` worker rows | replaces the whole worker vector at `+0x160` (0xb8 stride) | confirmed |
| 1 | `u8 A`, `u8 count`, then `count` recruits | the recruit list at `+0x178` | confirmed |
| 2 | `u8 slot, u8 port row, u8 failure row, 16 × u32` | one dock slot (`+0x2c0 + 96·slot`), the 16 u32 indexed by `VillageExports` row | confirmed |
| 3 | `u8 idx, 3 × u64` | `+0x3e0 + 24·idx` | candidate |
| 4 | the 245-byte state block, then `u64 sentTime` | the counters at `+0x190`…`+0x22f` and the clock at `+0x190` | confirmed |
| 5 | one worker row | appends to the worker vector at `+0x160` | confirmed |
| 6 | `u8 idx` | erases the worker at that index | candidate |
| 7 | `u8 idx, u8 nameKey` | selects the worker row | candidate |
| 8 | `u8 slot` | the selected dock slot (`+0x5a0`, `+0x5a1`) | candidate |
| 9 / 10 | `u8 port row, u8 n, n × u32` | the port's 6-number quota | candidate |
| 11 / 12 | `3 × u8` | two hash containers keyed by 3 bytes | candidate |
| 13 | `u8 idx, string` | `+0x470 + 32·idx` | candidate |

The **worker row** is 13 bytes plus one job byte: `u32 name1 | u32 name2 | u16 wage | u8 pairCount | pairCount ×
(u8 jobType, u8 skill)` and then the `VillageJobs` row index. The name hashes resolve to the row's display name; the
pair's job type and the row's job index must agree or the client draws the row blank. **confirmed**.

The **recruit element** is the same 13 bytes plus a `u32` **hire price** (the price is the card's "Cost:" line). The
count byte is read twice — once by the handler, once by the reader — so the frame carries it twice; a frame that
carries it once makes the reader run past the blob and the client throws. **confirmed** (the throw is what the earlier
"the client crashes on the recruit list" reports were).

The **state block** (sub-tag 4) is read as 166 bytes (the reader stops at `0xa6`), but its declared length has to cover
245 bytes or the client raises "Buffer Underflow", so everything past `0xa6` is zero padding. In order: 21 `u32`
(`VillageResources` rows, the store), 15 `u32` (`VillageProduction` rows Mining1..5, Smelting1..5, Farming1..5), the
treasury `u32`, a spare `u32` at `+0x198`, six slot bytes (the `VillageResources` row of each farm plot: 10..14 for a
crop, `0xff` bare — **never 0**, row 0 has no name object and the label resolver `0x1404f9e60` faults at `+0x8`), then
the timestamp the client advances the town by as a `u64`. All little-endian; the timestamp must be whole Unix seconds (a
zero makes the client log "advancing village state to a time in the past" and crash). **confirmed** — except the spare
word: writing the wage total there crashed the client in the shipping failure roll (`0x162869f`), so it stays 0.

**Sub-tag 9 takes the client down** (three times, 2026-11-22). The payload format is right; the crash is an embedded
widget built by `0x1402d0a90` (its own vtable `0x143062e68`) whose constructor looks an id up in a keyed table at
`supplier+0x168` (`0x141d65830`, 24-byte rows, binary search) and dereferences the result even when the id is missing
(`c0000005` at `0x1402d1029`). The table that would key it is encrypted on disk, so this one cannot be finished from the
executable alone.

`Modules/Core/Village/VillageModule.cs` builds these frames (`StateFrame`) from the settlement it keeps in memory, and
`data/kingsmarch-state.txt` still lets a hand-written frame be tried against a live client without a rebuild. Sub-tag 9
stays unused until the missing table is understood.

## The port window

`0x7b` kind `0x58` shows the harbour: a parchment map of the seven ports and three ship cards (大海勇氣, KTS 東方之星,
卡迪根之怒), each with `船隻: n/6` (crew), a `貨物` box, a destination line and one button. The cards are drawn from
sub-tag 2 above, re-read on every draw: a `setcargo` probe showed a card's `貨物` box take a crop icon and a count
straight out of its dock slot, and a pushed frame with slot 0, port row 1, failure row 6 and 40 Wheat drew the whole
route row (destination `里本丘原`, travel time `01:47:00`, wage `0/h`) with a `啟航` button.

**But the window's buttons never build a village action.** With the `0x2ca` builder sites (`0x141f9de33`,
`0x1421a1985` — the only two callers of the message constructor `0x141fa7a10`) and the dispatcher (`0x14176a8e0`) all
hooked, clicking `貨物`, `選擇港口`, `啟航` and `遺棄` (including the "abandon this cargo?" confirm) produced no message,
no log line and no change to the harbour's save; the card's `航行中/靠岸` state and that dialog are the widget's own local
state. The recruitment window (`0x55`) does send from the same hooks — a click on `僱用` arrived as
`0x02ca village action 0x01 hire worker … blob 0101` — so the channel works and the port window is the exception.

So the window is the readout and `/port …` is the input: `route <slot> <port>` picks a card's destination, `load
<slot> <crop> <count>` moves crops out of the farm's store into that card's `貨物` box, `sail <slot> [port]` sends it
(tying up its crew, `ceil(value / ExpectedShipmentValue)` hands, 1…6), `unload <slot>` puts the cargo back, and a trip
that is due banks its barter and gives the slot back. The dock slots live in `data/kingsmarch-port.json`
(`SavedPort.Docks`), the crew and barter beside them.

## The farm window

`0x7b` kind `0x59` shows one plot's farm page: a crop icon with `目前正在耕種`, a crop dropdown, `每小時產量` and `總數`.

**Unlike the port window, this one does send.** Picking a crop from the dropdown arrives as `0x2ca` action `0x04`,
`blob 04 <plot> <row>`, where the first byte is the plot order from 0 and the second is a `VillageResources` row:
10..14 for Wheat, Corn, Pumpkin, Globulora and Orbisfera in the town's own crop order, `0xff` for the list's 無 entry.
Live 2026-11-26: picking Corn on the first plot arrived as `04 00 0B`, and picking 無 as `04 00 FF`. The same action
byte also carries the assign button (`u8 worker, u8 plot`), whose bytes are 0..5, so the two do not overlap and
`VillageModule.Farm` tells them apart by range.

The action lands in `KingsmarchTown`: a row of 10..14 is `Choose` (plant that crop on that plot), `0xff` is `Fallow`
(take the crop off and leave the plot bare), anything else is `AssignIdle` (put that worker on that plot).
