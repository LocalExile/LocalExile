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

- **NPCs.** `NpcCatalog`'s Kingsmarch table places the nine villagers on the cells their own room lines give them, as
  offsets from the arrival (590, 655). Their types list `ControlZone`, `Brackets` and `Functions`, which deserialize
  through the same no-op reader as `BaseEvents` (`0x1400f1180`, `ret 0`), so they cost the blob nothing.
  `VillageVisibilityController`'s `on_or_create_state_visible_0` runs `DisableTargetable()`, `DisableHighlighting()` and
  `HideMinimapIcon()`, and `visible` starts at 0, so the server sends `visible = 1` with the "keep the sent value" flag.
- **Farm plots.** `FarmingPlot` (`read 6`) is a server object: its StateMachine's `plant` state selects the crop model, and
  the model swap lives in the client's `on_or_create_state_plant_<n>` events, so a crop change is a remove (`0x2bd`) and
  a create (`0x2bb`) with a **new id** — re-creating the same id leaves the old model. A plot is never removed twice
  within a few seconds. `num_workers` is the same StateMachine.
- **Stash and guild stash** stand on the room's own cells (420, 611) and (420, 641).

## The village UI cannot be opened in this build

- The panel message is S→C `0x7b` with a kind byte (`InstanceWaypoints.OpenUi`). The client's screen table
  `0x142fd89f0` names the Settlers kinds: `0x54` Recombinator, `0x55` Recruitment, `0x56` TownMap, `0x57` Mine,
  `0x58` Port, `0x59` Farm, `0x5a` BlackSmith, `0x5b` Disenchanting, `0x5c` MapRunners, `0x5d` Fishing,
  `0x5e` CurrencyExchange, `0x5f` PassiveTree.
- The windows are created unconditionally at HUD init (`0x140dad910`): the town map is `[hud+0xa00]`
  (0x540 bytes, ctor `0x1405235c0`, Show `0x140529450`), the port is `[hud+0x9c0]`, the farm `[hud+0x9d8]`,
  the recruitment window `[hud+0x9b0]`. Kind `0x56` reaches the HUD handler `0x140ddd6f0` and calls Show, and Show
  early-outs while `[win+0x2e0]` (the village rules pointer) is null. Only the recruitment window builds its own
  `+0x2e0` (ctor `0x14050b800`); the others never get one, and the client's own Show call sites sit in a code span
  (`0x140d95000-0x140d95c50`) that nothing references.
- `ClientVillage` is created by the client in the area-load function (`0x140d57380`, 0xe208 bytes, vtable `0x14346c648`),
  and **no message writes its state**: the treasury gold field `+0xc40c` is only ever zeroed by its constructor
  (`0x14214dada`), the other 43 references read it. S→C `0x2c9` is stored as a list of handles (reader `0x141fa7ca0`) and
  is not in the instance dispatcher's table at all; the village action channel C→S `0x2ca` is what the panels send
  (`0x18`/`0x19` give/take treasury gold, `0x01` hire, `0x04` assign farm, `0x05` upgrade), which the server can answer.
- Net: with this client a Settlers panel would be an empty shell (its gold cell can never show a number). The hub is
  therefore driven by objects and commands, and a PR that wants the panels needs a client patch that fills
  `[win+0x2e0]` and a server that writes `ClientVillage`'s state, neither of which exists.

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
