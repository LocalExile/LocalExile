# Settlers of Kalguur: Kingsmarch

What the 3.29.3c client does with the Settlers hub (`KalguuranSettlersLeague`, HASH16 `0x8f53`), and what the server has
to send because the client will not build it itself. The league's village UI is **dead code in this build**, which shapes
everything below: the panels are opened by a server message, but the client never creates the windows behind it, so the
hub is driven by objects and chat commands instead.

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

## The village UI opens, with empty numbers

- The panel is the client's own window: the HUD creates it at init (`0x140dad910`) — the town map is `[hud+0xa00]`
  (0x540 bytes, ctor `0x1405235c0`, Show `0x140529450`) — and the game's "Toggle Town Map" button opens it. The
  server message `0x7b` (kind byte) is a different path: its screen table `0x142fd89f0` names `0x56` TownMap, but
  kind `0x56` reaches the HUD handler `0x140ddd6f0`, which calls Show on the **world** map window; the client's own
  button calls the town map's Show directly.
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
  and **no message writes its state**: the treasury gold field `+0xc40c` is only ever zeroed by its constructor
  (`0x14214dada`), the other 43 references read it. The record the server builds is empty, so the panel opens with
  zeroes (population 0/90, gold 0); the message that fills the record's rows (`+0x160`) and its gold is not known yet.
  S→C `0x2c9` is stored as a list of handles (reader `0x141fa7ca0`) and
  is not in the instance dispatcher's table at all; the village action channel C→S `0x2ca` is what the panels send
  (`0x18`/`0x19` give/take treasury gold, `0x01` hire, `0x04` assign farm, `0x05` upgrade), which the server can answer.
- Net: the panel opens and draws, but its numbers are empty; the hub is driven by objects and commands until the
  message that fills the settlement record is known.

## What the server implements instead

- `Modules/Core/Game/World/KingsmarchTown.cs`: the town's server objects (farm plots, then optionally the noticeboards),
  crop growth per plot, workers, wages, the treasury, and a click on a plot to plant or harvest.
- `Modules/Core/Game/World/KingsmarchPort.cs`: the harbour — the seven ports of `VillageShippingPorts`, cargo value from
  `VillageExports`, crew, travel time and risk from distance, and the failure rows of `VillageShippingFailures`.
- Commands: `/village` (report), `/village hire`, `/village assign <worker> <plot>`, `/recruit`, `/treasury`,
  `/port` (report), `/port send <port> <crop> <count>`, `/port crew <n>`, `/port storm <percent>` (dev), `/unlock`.
- `Modules/Core/Game/World/UnlockAll.cs`: grants the NPCTalk gate flags a completed campaign would have set (see
  [progression](../../status/progression.md)); `data/unlock-all.txt` makes the safe set automatic.

## Data

`data/kingsmarch-town.json` is generated by `scripts/dev/kingsmarch_town.py` from `client.db`'s `room_doodads`
(room 1984): 444 rows of `{type, model, x, y, angle}` plus the room origin (138, 138). It is the input the town's
spawner walks; it is not committed, `scripts/dev/extract-data.ps1` writes it.

## The village state channel (S→C `0x2c9`)

Every number a Kingsmarch panel draws that is not a record comes from `0x2c9`: a frame is
`u16 0x2c9 | u8 kind | compact-int length | blob`, where `kind` is the ruleset record kind (`0x9b` = the town record) and
the blob is one sub-tag byte plus its payload. `ClientVillage` hands the blob to its vtable slot 8 (`0x141767f60`), which
compares that byte against 1…13 in a chain (`1→5→6→7→4→2→3→9→10→11→12→13→8`) and **never resynchronises**: one frame it
cannot parse ends the connection. The server had no `0x2c9` sender at all, which is why every panel opened with the
client's own defaults (population 0/90, no quotas).

The sub-tags, with the offsets they write (full table in `Tools/re/tmp/kmre/KALGUUR_SHIPPING_FRAMEWORK.md` §4):

| sub-tag | payload | writes |
|---|---|---|
| 1 | `u8 n` | resizes the 176-byte record vector at `+0x178` to `n` |
| 2 | `u8 slot, u8 port row, u8 failure row, 16 × u32` | one dock slot (`+0x2c0 + 96·slot`), the 16 u32 indexed by `VillageExports` row |
| 3 | `u8 idx, 3 × u64` | `+0x3e0 + 24·idx` |
| 4 | timer payload + `u64 sentTime` | `+400 += now − sentTime` |
| 5 / 6 / 7 | one 184-byte worker record / `u8 idx` / `u8 idx, u8 job row` | append, remove, re-job the worker vector at `+0x160` |
| 8 | `u8 slot` | the selected dock slot (`+0x5a0`, `+0x5a1`) |
| 9 / 10 | `u8 port row, u8 n, n × u32` | the port's 6-number quota |
| 11 / 12 | `3 × u8` | two hash containers keyed by 3 bytes |
| 13 | `u8 idx, string` | `+0x470 + 32·idx` |

**Sub-tag 9 takes the client down** (three times, 2026-11-22). The payload format is right; the crash is an embedded
widget built by `0x1402d0a90` (its own vtable `0x143062e68`) whose constructor looks an id up in a keyed table at
`supplier+0x168` (`0x141d65830`, 24-byte rows, binary search) and dereferences the result even when the id is missing
(`c0000005` at `0x1402d1029`). The table that would key it is encrypted on disk, so this one cannot be finished from the
executable alone.

`Modules/Core/Village/VillageModule.cs` sends these by hand (`StateFrame`), driven by `data/kingsmarch-state.txt`, so a
frame can be tried against a live client without a rebuild. Sub-tag 9 stays commented out there until the missing table
is understood.
