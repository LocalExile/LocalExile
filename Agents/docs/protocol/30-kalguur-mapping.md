# 30. Kingsmarch: the Mapping window (君峰镇 异界冒险)

State of 2026-11-23: the Gateway sends no Kingsmarch settlement data at all, so the client's Mapping window — the
Kingsmarch map device, which the client's screen table names `KalguurMapRunnersWindow` and the Chinese client calls
异界冒险 — draws itself from `ClientStrings` alone and **every one of its buttons is a no-op**. This page records the
client-side implementation, reverse-engineered for a fix: the window's object graph, the two panel fields that gate
every click, the C→S message every button sends (`0x2ca`) and the opcodes inside it, and what the server has to send
before the buttons do anything. Nothing here is implemented on the server yet; C→S `0x2ca` has no handler in the Gateway.

```mermaid
sequenceDiagram
    participant C as Client
    participant G as Gateway
    C->>G: 0x24 on the Kingsmarch map device (today: not sent for this area)
    G->>C: 0x1f village record, 0x7b kind 0x5c (KalguurMapRunnersWindow) — today: not sent
    Note over C: panel +0x2e0 set, +0x371 set, a device index selected
    C->>G: 0x2ca, payload 0x0c <device index> (Open Map; 0x0e + flag suspends/resumes)
    G->>C: village mapping state (0x1f / 0x7b) → panel refresh
```

The client-side sound and state feedback for the same action does not go over the wire: it is played by
`FUN_14054d050` (`SettlersLeague_MappingOpen`) inside the village object, and its counterpart `FUN_14054d400`
(`SettlersLeague_MappingClose`) runs only as panel vtable slot 50, so both are reachable only once the panel holds
data.

The other Kingsmarch settlement windows - Recruitment (`0x55`), Harbour (`0x58`), the Rewards window they share
with Mapping, and the per-row action handlers of the production windows - are in
[31](31-kalguur-settlement-windows.md); the object-graph and message-order rules there apply here unchanged.

## 1. The window and its object graph

The Mapping panel is a 0x398-byte control whose vtable is `0x1430d22c0`; it lives inside a 0x2f0-byte window
(vtable `0x1430e0438`) at window `+0x2e8`.

| Address | What |
|---|---|
| `0x140d9e5c0` | window factory: allocates 0x2f0, `FUN_140773010` base ctor, then `[0x5d] = FUN_140555630(this + 0x17)` (the panel) |
| `0x140d632c0` | Settlers UI manager ctor: builds every Settlers window eagerly, one factory per slot; the map-runners window (`0x140d9e5c0`) is stored at `manager + 0x618` |
| `0x140dad910` | 34418-byte Settlers dispatcher: calls `FUN_140555630` once for a panel of the same class (HUD side); it does **not** build the map-runners window |
| `0x140555630` | panel factory: allocates 0x398 and runs the panel ctor; appends to a vector |
| `0x14054a2f0` | the panel ctor (11283 bytes): captions every sub-control from `ClientStrings` and registers its click handler |
| `0x14054cf10` | deleting dtor, vtable slot 8 |
| `0x14054d120` | per-frame tick, vtable slot 32 |
| `0x14054d400` | "mapping close" path, vtable slot 50 (no static caller) |

The ctor captions a control with `FUN_1422045d0(this, <ClientStrings id>)` and registers a click handler with
`FUN_14020f250(control + 0x298|0x299|0x29a|0x2a0, panel, handler)`.

**The id in code is `ClientStrings._row + 1`, not the row number** (the table is 1-based and the client indexes it at
`row - 1`). Two independent proofs in the ctor: the window title is `0x1d07` = row 7430 `KalguurMappingWindowTitle`
"Mapping", and control `[0x61]`'s hover is `0x1d00` = row 7423 `KalguurEditWorkersButtonHover` "Manage Workers".
Every label below is therefore renumbered: the button group is `[0x67]` 0x1d09/0x1d0a = row 7432/7433 "Open Map" +
"Atlas Runners will run Maps", `[0x69]` 0x1d0b/0x1d0c = row 7434/7435 "Exit Map" + hover, `[0x6b]`
0x1d0d/0x1d0e = row 7436/7437 "Suspend Mapping" + "Pauses Mapping once the in progress Map is complete", `[0x6d]`
0x1d51/0x1d52 = row 7504/7505 "Resume Mapping" + hover; tab `[0x62]` is 0x1d08 = row 7431 `KalguurMappingInfoHover`.
Each control gets its caption through vtable slot `+0x10` of `+0x3f8` and its hover through
`FUN_1418f17c0(control, 1, &zero, &text)`. "Click to Take Rewards" (row 7500 = id 0x1d4d) is **not** used by this
ctor — that button is built by the rewards child.

Panel fields that matter:

| Field | Meaning |
|---|---|
| `+0x2e0` | the **settlement object** the window was handed (the `0x9B` mapping record, 0x5A8 B, ctor `0x141767ba0`, vtable `0x14348e820` - or the village that holds it, see "how the panel is handed its data" in §5); null until the server creates the settlement, and its `+0x190` block (0x110 B) holds the per-slot data the panel renders (layout in §4) |
| `+0x2d0` | **`ClientVillage`** itself: it carries the treasury gold at `+0xc40c`, the resolution table at `+0xc890` and the 4-byte handle table at `+0xc9d8`/`+0xc9e0`/`+0xc9e8` |
| `+0x370` | selected mapping-device index (byte) |
| `+0x371` | "have a selected device" — the gate of every button handler |
| `+0x372` | "the panel is open" (written as 0x01 0x01 together with `+0x373` on open) |
| `+0x373` | "refresh pending" — the tick turns it into a `FUN_14054de50` refresh |
| `+0x374` | accumulated tick seconds (float), compared with `DAT_1436adf9c` |
| `+0x380` | the per-device detail child (0xa30 bytes, vtable `0x1430d9930`, owner at `+0x388`) |
| `+0x390` | "mapping closed" flag, read by the close path before `SettlersLeague_MappingClose` |
| `+0x1e8` bit 11 | the panel's "enabled" bit, set by the HUD with `FUN_1418b0a30(panel, 0)` |

`FUN_14054d120` (tick) returns unless `+0x1e8` bit 11 is set; it turns `+0x372` into `FUN_14054d9a0` (populate the
device/job list) and `+0x373` — or an elapsed `DAT_1436adf9c` — into `FUN_14054de50` (refresh from the snapshot
`FUN_1417690f0` copies out of the village: **0x110 bytes** at village `+0x190`, then `FUN_1424c4550` fills the rest
from village `+0x160` and `+0x2a0`). The 0x110 block's layout is given in §4 (36 dwords, six bytes, three qwords, three 0x18-byte records, a status byte) — the refresh indexes
it as `auStack_9a[idx*6]` (dwords) and `auStack_8a[idx*3]` (qwords), the same 24-byte stride. `FUN_14054e550(panel, byte)` is the only
writer of a valid selection: it stores `+0x370 = index` and `+0x371 = 1` (as one 16-bit store), rebuilds the child at
`+0x380` from the device record and sets `+0x373`.

## 2. Why the button does nothing

Every click handler of the window (and of the sub-panels it hosts) starts with the same two tests, and returns when
either fails:

```c
if (*(longlong *)(panel + 0x2e0) == 0) return;   /* no settlement record     */
if (*(char    *)(panel + 0x371) == 0) return;    /* no selected device index */
```

The three handlers on the first button control (`+0x298`/`+0x299`/`+0x29a` of `param_1[99]`) and the one on
`param_1[100]` add a third test, `((*(uint *)(*(longlong *)(panel + 0x318) + 0x1e8) >> 11) & 1) != 0` — bit 11 of the
worker sub-object's enabled field (`param_1[100]` reads it from `+0x320`). `+0x2e0` is written in the panel family by the widget/host ctors
(`0x140521da4`, `0x14052285e`, `0x140522a57`, `0x140529060`, `0x14052e8f2`, `0x14053360b`, `0x1405346d1`,
`0x140540ee2`, `0x140543910`, `0x140546872`, `0x1405480c2`, `0x1405546bd`, `0x140568392`, `0x140570272`,
`0x140571ea4`, `0x14057398f`, `0x14057ea32`) and cleared by the dtor (`0x14054cfc0`).

So the visible window is pure UI: the labels, the tabs and the gamepad navigation work, and a click does nothing until
the client has (a) the settlement record at `panel+0x2e0`, (b) data inside it (`panel+0x371`), and (c) a selected row
index. On today's Gateway all three are missing, because nothing sends the `0x7b` kind `0x5c` or the `0x1f` record that
creates the settlement record — which is exactly the reported symptom.

## 3. C→S `0x2ca`: the message every button sends

All the window's action buttons go through one primitive, `FUN_1421a1960(data, gadget)`:

```c
FUN_141fa7a10(msg);                       /* message object, id 0x2ca            */
plVar2 = gadget[7];
(*(code *)(*plVar2 + 0x10))(plVar2, …);   /* append the opcode and its arguments */
FUN_1421752b0(data + 0x10, msg);          /* write to the instance stream        */
FUN_140dfa160(stream + 0xc288, 0, 0);     /* flush                               */
```

`FUN_141fa7c10` serialises it as **u16 BE `0x02ca`, u8 `0xaf`, compact length, payload** (`0xaf` is a constant the message ctor stores at `+0x90`, meaning not identified; the id itself is big-endian,
`htons`). `c2s.json`'s `0x2ca` entry
(`vtable 0x1435cb1d0`, `write 0x141fa7c10`, sender `0x1421a1960`) matches. The payload is a sequence of
`opcode` + fixed arguments, written by per-button gadget descriptors in `.data` (`0x14348d…`, four qwords each:
serialise, serialise, append, destroy):

| Opcode | Arguments | Writer | Button (caption rows in the panel ctor) |
|---|---|---|---|
| `0x04` | 1 byte + list count (0xff when empty, count = bytes / 0x2c) | `0x141778100` | device selection change (`FUN_1405479c0`) |
| `0x0c` | 1 byte (device index) | `0x141777aa0` | `FUN_14054d650`, ids 0x1d09/0x1d0a = rows 7432/7433 ("Open Map") |
| `0x0d` | 1 byte | `0x141777a10` | `FUN_14054d6b0`, ids 0x1d0b/0x1d0c = rows 7434/7435 ("Exit Map") |
| `0x0e` | 1 byte index + 1 byte flag | `0x141777950` | `FUN_14054d710` flag **1** = ids 0x1d0d/0x1d0e = rows 7436/7437 ("Suspend Mapping"), `FUN_14054d780` flag **0** = ids 0x1d51/0x1d52 = rows 7504/7505 ("Resume Mapping") |
| `0x0f` | — | `0x141777d70` | `FUN_14054d850` (control `[99]` +0x298), also gamepad key 8 |
| `0x10` | — | `0x141777d20` | `FUN_14054d8c0` (control `[99]` +0x299), also gamepad key 0xd |
| `0x11` | — | `0x141777cd0` | `FUN_14054d7f0` (control `[99]` +0x29a) |
| `0x1d` | 1 byte index | `0x1417778c0` | `FUN_14054d920` (control `[100]` +0x298) |

The gamepad path (`FUN_14054ec40`, key events `param_2[4]`) reuses the same set: key 8 → `0x0f` (the same
descriptor `0x14348dd08` as `FUN_14054d850`), key 0xd → `0x10` (`0x14348dbf8`, `FUN_14054d8c0`), key 9 →
`FUN_14054ea40`, which sends `0x1d` with the selected index when `+0x318` is disabled but `+0x320` enabled, and
`0x11` when `+0x318` is enabled.

`ClientVillage` has exactly four mapping APIs (`Rulesets@Game`, each taking the device index; RTTI lambdas at
`0x1444dee10`, `0x1444de380`, `0x1444de4d0`, `0x1444de7e0`): `StartMapping(uchar)`,
`SetPauseMappingAfterCurrentMap(uchar, bool)`, `InterruptMapping(uchar)` and `OpenMappingRewards(uchar)`. The opcodes
above are the buttons' captions as the ctor attaches them, and the ctor C settles the order:

| Opcode | Button (ClientStrings) | Client API |
|---|---|---|
| `0x0c` + index | "Open Map" | `StartMapping(index)` |
| `0x0d` + index | "Exit Map" | `InterruptMapping(index)` |
| `0x0e` + index + 1 | "Suspend Mapping" | `SetPauseMappingAfterCurrentMap(index, true)` |
| `0x0e` + index + 0 | "Resume Mapping" | `SetPauseMappingAfterCurrentMap(index, false)` |
| `0x1d` + index | (rewards child) | `OpenMappingRewards(index)` — `candidate`, now with a corroborating client-side path: the settlement context menu (`FUN_14054eef0`) adds a "Click to Take Rewards" entry (ClientStrings id `0x1d4d` = row 7500) exactly when the snapshot row's `+0x14` byte, the pending-reward count, is non-zero |
| `0x04` + byte + count | device change | not one of the four (a list update) |
| `0x0f` / `0x10` / `0x11` | control `[99]`'s three actions | unidentified; no argument, so no device index |

The four label captures are static: `FUN_14054de50` contains no `ClientStrings` access at all, so state is expressed
only through enable/hide (the `+0x1e8` bit 11 gates), never by swapping captions. A tempting shorter route to the
opcode order — decompile the four `ClientVillage` API bodies and see which gadget descriptor each references — is
**falsified**: `xrefs.py vtable` over the eight `0x14348d…`/`0x14348e…` descriptors returns 21 sites, all inside the
UI handlers and the gadget serialiser functions themselves. The opcodes are built entirely UI-side.

## 4. How the window is opened

Two functions run the open path; neither has a static caller (both are virtual/HUD entries):

| Function | What it does |
|---|---|
| `0x14054d050(village_obj, byte mode)` | requires `param_1[0x5c]` (the mapping record) and `mode < 3`; writes `+0x372/+0x373 = 1`, selects the device with `FUN_14054e550`, plays `SettlersLeague_MappingOpen` (unless `+0x1e8` bit 11), calls vtable `+0x190`, notifies `param_1[0x3c]` (`+0x1e0`) and sets `+0x72` |
| `0x14054d400(panel)` | the close path: `FUN_14015e460`, `FUN_14054e550`, and `SettlersLeague_MappingClose` when `+0x390` was set |
| `0x140ddd910(hud, byte)` | HUD entry: needs `hud + 0x9e8` (the panel), then `FUN_140dc45b0`, `FUN_1408f4a10`, `FUN_140dc2640`, **`FUN_14054d050(hud + 0x9e8, mode)`**, `FUN_1405092c0(hud + 0xa08)` |
| `0x140d95fd0(manager, byte)` | the Settlers entry: requires `manager + 0x618` (the map-runners window, see below), writes `0x1f` into `*(u8 *)(stream + 0x2f5)`, clears the current screen (`FUN_140776a10`), then **`FUN_14054d050(*(manager + 0x618) + 0x2e8, mode)`** and finally shows the window with `FUN_140774d20(*(manager + 0x310), *(manager + 0x618), 0, 1, 1, 0)` |

The `mode` byte chooses one of three mapping modes; the panel is `window + 0x2e8` (the host object's `[0x5d]`).

The windows are **built once, at manager construction**, not per `0x7b` message: `FUN_140d632c0` (the Settlers UI
manager ctor) calls a chain of factories and parks each result in a slot of the manager — `FUN_140d9dfc0` →
`+0x5f8`, `FUN_140d9e140` → `+0x600`, `FUN_140d9e2c0` → `+0x610`, `FUN_140d9e440` → `+0x608`, then
**`FUN_140d9e5c0` → `+0x618`** (`0x140d9e5c0` is the 0x2f0-byte host window that creates the `KalguurMapRunnersWindow`
panel at `+0x2e8` via `FUN_140555630`), `FUN_140d9e740` → `+0x620`, `FUN_140d9e8c0` → `+0x628`, `FUN_140d9ea40` →
`+0x630`, and so on. A `0x7b` kind therefore *selects and shows* an existing window; it does not construct one. The selection itself is virtual: `FUN_140d95fd0` (the open function) has no direct caller anywhere in the image — a `grep` of the whole decompiled tree finds only its definition — so the Settlers manager opens a screen through a vtable slot, and the `0x7b` kind never reaches a static table.

### The record the panel reads

`*(panel + 0x2e0)` is the settlement **record** (the `0x9B` ruleset object, 0x5A8 bytes, ctor `FUN_141767ba0`,
vtable `0x14348e820`). The panel's refresh passes it straight to `FUN_1417690f0(record, local)`, whose decompiled body
copies `record + 400` — decimal, i.e. **`record + 0x190`** — as two 0x80-byte blocks plus a 0x10-byte tail, 0x110 bytes
in all, into the local buffer the refresh then indexes.

The layout of that 0x110 bytes is not a guess: the record's own wire serializer `FUN_141767f60` (vtable slot `+0x40`)
has a case `'\x04'` whose body is `FUN_1424c8190(reader, record + 0x190); … *(longlong *)(record + 0x190) +=
_time64() - wireTime;`, and `FUN_1424c8190` reads the block in this order `[confirmed]`:

| offset | content |
| --- | --- |
| `+0x00` | qword timestamp, sent as a delta from the client clock and re-based with `_time64()` |
| `+0x08` | dword |
| `+0x0c` | dword |
| `+0x10..+0x9f` | 36 dwords read as two contiguous runs (0x10..0x60 then 0x64..0x9c) |
| `+0xa0..+0xa5` | 6 single bytes |
| `+0xa6`, `+0xae`, `+0xb6` | three qwords, each through `FUN_140df7350` |
| `+0xbe`, `+0xd6`, `+0xee` | **three** 0x18-byte records through `FUN_1424c8020` (stride 0x18, ending at `+0x106`) |
| `+0x106` | qword |
| `+0x10e` | byte: low nibble and high nibble from two separate wire bytes |
| `+0x10f` | the packed status byte the refresh tests |

Those three 0x18-byte records **are** the rows the panel renders, and the refresh's indexing proves it: its local
buffer starts at `auStack_158`, and the two arrays it walks are `auStack_9a` (= buffer `+0xbe`, dwords) and `auStack_8a`
(= buffer `+0xce` = row `+0x10`, qwords), with `uVar11 = *(byte *)(panel + 0x370)`, the selected slot. So
`auStack_9a[idx*6]` and `auStack_8a[idx*3]` are the row's four dwords at `+0x00..+0x0c` and the qword at `+0x10`.
`FUN_1424c8020` fills that row as `[confirmed]`: `+0x14` is a byte count — the pending-reward count, because the
settlement context menu (`FUN_14054eef0`, `acStack_46[idx * 0x18]` = snapshot `+0xd2 + idx*0x18` = row `+0x14`)
adds the "Click to Take Rewards" entry (id `0x1d4d`) only when it is non-zero — `+0x15` and `+0x16` are two single flag bytes,
the qword at `+0x08` and the dword at `+0x10` are zeroed first, and then `count` single bytes are copied into
`+0x08..+0x0f`. Since the qword the refresh reads at `+0x10` covers `+0x10..+0x17`, its byte 5 and byte 6 are exactly
`+0x15`/`+0x16` — the "running" and "pending" flags the button enables derive from. An earlier revision of this document
said "ten 0x18-byte device records"; that was an inference from the stride alone and is **wrong** — the block carries
**three** rows at `+0xbe`/`+0xd6`/`+0xee`, i.e. three map slots, plus the 36-dword array at `+0x10..+0x9f`.

### What the three rows are `[confirmed]`

The client's own data tables name them. `VillageJobs` has a `JobType` column, and rows 18-20 are
`('Mapping', Stat 20725, StatValue 1|2|3)`; `VillageTownLayout` matches those three with
`Mapping0/Mapping1/Mapping2` - `Map Device 1`, `Map Device 2`, `Map Device 3`, art
`Art/2DArt/UIImages/InGame/Village/VillageMap/MapDeviceSubJobButton` under the parent `MappingParent` ("Map Devices",
art `.../VillageMap/MappingJobButton` at 1084,1084). `VillageJobTypes` gives `5 = Mapping` (icon
`JobIcons/MapRunningJobIcon`, `MaxWorkersStat` 20726, `JobSpeedStat` 20699) and `6 = Shipping`.

That is exactly how the snapshot is decoded: the row-list builder `FUN_1424c32e0` walks the three 0x18-byte rows at
`+0xbe` and asks `FUN_1424c7900(&key, 5, i)` for each - job type **5**, index `i` - while the three qword pairs at
`+0xa6`/`+0xae`/`+0xb6` are looked up with the same helper and job type **6** (the three boats, `VillageJobs` rows
21-23 / `Shipping0..2`). `FUN_1424c7900` reads `Data/VillageJobTypes.dat` (`decomp/index.tsv` records it as
`0x1424c7900  373  FUN_1424c7900  5  ok  Data/VillageJobTypes.dat`), so "group 5/6" is a job type, not an opaque id.

Each row is then a `(current, required)` pair plus state: `FUN_1424c32e0` tests
`uVar21 = *(uint *)(param_2 + 0xc2 + i * 0x18)` (row `+0x04`) against `*(uint *)(param_2 + 0xbe + i * 0x18)` (row
`+0x00`) and treats the row as *available* while `+0x00 < +0x04`, skipping it when the status byte's bits 5-7 name it as
the running device (`((*(byte *)(param_2 + 0x10f) >> 5) - 1) == i`); the same test with `>> 1 & 7` runs for the three
boat pairs at `+0xa6`.

| field | meaning `[confirmed]` |
| --- | --- |
| `+0x08` | run duration in hours - `FUN_1424c32e0` computes `(*(uint *)(param_2 + 8) * 0xe10 - *(uint *)(param_2 + 0xc)) / divisor`, `0xe10` = 3600 |
| `+0x0c` | elapsed seconds of the current run (the subtrahend above) |
| `+0xa6`/`+0xae`/`+0xb6` | the three **boats**: each qword is `(current, max)` as two `u32` (`+0xa6 + i*8`, `+0xaa + i*8`) |
| `+0xbe`/`+0xd6`/`+0xee` | the three **map devices**: `+0x00` current, `+0x04` required, `+0x08..0x0f` payload, `+0x14` pending-reward count, `+0x15` running, `+0x16` pending |
| `+0x106` | another `(current, max)` pair, same shape as the boats |
| `+0x10e` | two packed nibbles (`b & 0xf \| c << 4`) |
| `+0x10f` | status byte: bits 1-3 = running device index + 1, bits 5-7 = running boat index + 1 (which is why the panel's `0x1f < value` test means "a map is running") |

### What the client actually reads out of the block `[confirmed]`

Every offset the decompiled client reads from this block, exhaustively (all 108,841 non-thunk bodies were
decompiled, and each of the ~46 callers of `FUN_1417690f0` was checked for accesses to its local copy):

| offset | reader |
| --- | --- |
| `+0x00` | the serializer delta (`FUN_141767f60`, `+400` field) |
| `+0x08`, `+0x0c` | `FUN_1424c32e0` — run duration in hours and elapsed seconds |
| `+0x64 + i*4` | `FUN_1424c32e0:204`, exactly one site, indexed by the element index of a 0x20-byte-element job list (`(ptr - base) >> 5`), stored next to `element[5] * 100` (a percentage) |
| `+0xa0..+0xa5` | `FUN_1424c32e0` and `FUN_1424c2e20`, six single bytes compared against `0x15` |
| `+0xa6 + i*8`, `+0xaa + i*8` (i = 0..2) | `FUN_1424c32e0` — the three boats' `(current, max)` |
| `+0xbe + i*0x18`, `+0xc2 + i*0x18` (i = 0..2) | `FUN_1424c32e0` — the three map devices' `(current, required)` |
| `+0x106`, `+0x10a` | `FUN_1424c32e0` — a fourth `(current, max)` pair |
| `+0x10f` | `FUN_1424c32e0` (running indices) and `FUN_14054de50` (the "a map is running" test) |

Two consequences follow. First, **nothing in the client reads the 21 dwords at `+0x10..+0x60`**: they are carried on the
wire, stored, and never rendered — a server may fill them with zeros without changing what the panel shows. The one
"VillageResources has 21 rows" match noted below is therefore a shape coincidence at best, not a reader.
Second, the 15 dwords at `+0x64..+0x9c` have exactly one reader, and it indexes them by *job-list element*, not by map
device, so they are per-job progress values rather than per-device fields.

The 36 dwords at `+0x10..+0x9f` split into two runs, and the first one looked like the settlement's resource
ledger `[unknown]` (see the table above - no client reader was found): `FUN_1424c8190` reads exactly 21 dwords into `+0x10..+0x60` and then 15 into `+0x64..+0x9c`, and
`VillageResources` has exactly 21 rows whose order starts `PendingIronOre`, `PendingCopperOre`, `PendingAmber`,
`PendingBismuthOre`, `PendingVerisiumOre`, then the five ores, the five crops (`Wheat`, `Corn`, `Pumpkin`, `Globulora`,
`Orbisfera`), the five refined metals and `MagicDust` - i.e. "what is waiting to be collected" first, which is what a
village snapshot would carry. No reader has been caught indexing it yet, so the match is a shape-and-count argument, not
proof. The 15 dwords at `+0x64..+0x9c` are indexed by table row in `FUN_1424c32e0`
(`*(int *)(param_2 + 100 + i * 4)`) but the table has not been identified. The six bytes at `+0xa0..+0xa5` are compared
against `0x15` and skipped when equal - a per-slot "no job" sentinel.

So the server-side shape is: a `0x1f` record of the mapping kind whose `+0x190` block matches the table above, plus the
`0x7b` kind `0x5c` message that opens the window.

### Selecting a row

`FUN_14054e550(panel, uint16 packed)` is the only writer of a valid selection: the low byte is the row index and the
high byte is the "valid" flag, so `+0x370`/`+0x371` are one 16-bit store. For a valid selection it then resolves the
device object `[confirmed]`:

```c
village = *(longlong *)(panel + 0x2d0);                        /* ClientVillage */
if (0x58 < (*(longlong *)(village + 0xc9e8) - *(longlong *)(village + 0xc9e0)) >> 2) {
  iVar2 = *(int *)(*(longlong *)(village + 0xc9e0) + 0x160);   /* element 0x58, dword at +0x160 */
  if (iVar2 != 0) {
    FUN_141d65830(village + 0xc890, &out, iVar2);              /* handle → object */
    lVar11 = FUN_14053b990(vtable?, village, *(byte *)(panel + 0x370), iVar2, &ctx);
    *(longlong *)(panel + 0x380) = lVar11;                     /* the 0xa30-byte detail child */
```

So the row index never indexes the handle table directly: the panel reads **one fixed element** (index `0x58`) of the
village's 4-byte handle table, uses its `+0x160` dword as a handle, and asks the village's resolver for the object.
That element is the mapping-device list itself; the per-row data comes from the record's `+0x190` block described above.

### How the panel decides which button is live

`FUN_14054de50(panel)` is the refresh that runs after every notification. It returns immediately unless the same two
gates as the handlers hold (`panel+0x2e0 != 0`, `panel+0x371 != 0`), then copies the village snapshot into a 0x110-byte
local with `FUN_1417690f0(*(panel+0x2e0), local)` and derives its state:

| local / field | meaning |
|---|---|
| `local[0x10f]` | a packed status byte: `<= 0x1f` means "no map running"; otherwise `(byte >> 5) - 1` is the index of the device whose map is in progress |
| `*(u8*)(panel+0x370)` | the selected device index (0–9) |
| `local[index*0x18 + 0..0xf]` | four dwords of the selected device's record |
| `*(u64*)(local + index*0x18 + 0x10)` | two flag bytes are read out of it (`_5_1_` and `_6_1_`) — the "running" and "suspended/pending" states |
| `*(u32*)(*(panel+0x2d0) + 0xc40c)` | the village treasury gold, compared against `FUN_1424c85f0(0)` (the mapping cost) to enable the start control; when it is short the control's hover is replaced by ClientStrings id `0x1cd5` = row 7380 "Insufficient Gold" (`FUN_1422045d0(&tmp, 0x1cd5)` in the refresh) |
| `*(panel+0x380)` | the selected **map device object**; `device+0xa22` is its "can run" flag, `device+0xa23`/`+0xa25`/`+0xa26` mirror the panel's state onto the device's own two buttons at `device+0x660`/`+0x668` (the second gated on bit 11 of `*(u32*)(*(device+0x660)+0x1e8)`, the same bit the panel's own `[99]`/`[100]` handlers check) |
| `*(panel+0x378)` | 0 when no worker is assigned: the status label at `panel+0x338` then shows id `0x1D94` = row 7571 `KalguurWorkersRequiredError` ("Job requires assigned workers") |
| status byte `> 0x1f` | the label shows id `0x1DB1` = row 7600 `KalguurMappingLockedByHostage` ("Opening maps is disallowed while Atlas Runners have been kidnapped at other map devices") |

The four controls are enabled from those bits: the first needs "device valid, no map running for this device, and the
device's running flag clear"; the second needs the pending flag; the third the running+pending pair; the fourth is
enabled only when the pending flag is set and the running flag is clear (`Resume`). A count taken from the snapshot
(`0 < aiStackX_18[0]`) additionally disables the start chain. None of this is visible to the server: it is all derived
client-side from the village data, which is why the panel stays blank until the village record arrives.

## 5. What the server has to send

The client reads everything from the object it is handed (`panel + 0x2e0`): its `+0x190` block is
the 0x110-byte snapshot laid out in §4, and that block is filled by the record's own wire serializer
(`FUN_141767f60`, case `'\x04'` → `FUN_1424c8190`), i.e. it comes from the `0x1f` message that creates the record.
Nothing else feeds the panel: the object at `*(village + 0x460)` belongs to a different feature (its `+0xc9d0` flag and
`+0xc9d8`/`+0xc9e0` begin/end pair are a generic 4-byte-element cache the settlement container shares with several
other windows), so a server implementation should not try to fill it.

The message itself is the plain `0x7b` frame already used by every panel ([16](16-waypoints.md) §"S→C 0x7b"): 19
bytes, `u8 kind` followed by four `u32` big-endian arguments, read by `0x1421a6dc0` (class `0x1435e71a0`) into
dispatcher case `0x1c` → `0x142169dd0`. The dispatcher's signature is `FUN_142169dd0(village, kind, a1, a2, a3, a4)` and kind `0x5c`'s
case notifies the frame with **`a1`** only (`uVar17 = (ulonglong)param_3; … FUN_140401df0(param_1 + 0x6678, uVar17)`),
so the first `u32` of the message is the payload the panel's listener receives; the other three are unused for this
kind. `a1` reaches the frame as a **plain 32-bit value** — `uVar17` is assigned exactly once, `uVar17 = (ulonglong)param_3`,
so the dispatcher zero-extends it and the listeners receive it in one register; the other three arguments are dead for
this kind. What the value means is `[unknown]`: the record's own attach method `FUN_141769f20` is *not* the path this
payload takes (it dereferences its argument at `+8`/`+0x10`, i.e. it expects a handle object, and is called from the
record's `0x1e` side), so the `+0x6678` frame's listeners still have to be identified. They have since been read, and they discard the value:
the Kingsmarch manager class's vtable (`0x1431f74d0`, whose slot 0 is `FUN_140d632c0`) has `FUN_140d6d300` at `+8` and
its payload handler at `+0x18` is `FUN_140d69e90`, declared `void FUN_140d69e90(longlong param_1)` — one argument, no
payload use, just a global-flag read at `*(*plVar5 + 0x209)` and refcount juggling. `grep -rln 0x6678` over the whole
dump finds only `FUN_142169dd0`, `FUN_140d6d300`, `FUN_140dba5d0` and two functions that dereference
`*param_1 + 0x6678` (an unrelated struct), so those two controllers are the only subscribers. `[candidate]`: for kind
`0x5c` the argument is vestigial on the client side. For the kinds already in use the
Gateway sends the relevant object id (kind 0 the waypoint object id, kind `0x4c` the 0-based class row), which is why
an object id is the working guess, not a verified fact. The frame's membership is known, though: subscribers join with
`FUN_1401a02f0(frame, listener, &node)` and leave with `FUN_1401a0260(...)`, and `FUN_140d6d300` (lines 453 and 2250)
does exactly that for `*(this + 0x2e0) + 0x6678`, with `this` = the object that owns the village pointer — so the
listener is that object, and its vtable slot `+0x18` is where the payload lands.

The delivery chain for a kind is now complete and has a single entry point `[confirmed]` (`dump/calls.tsv` records
exactly one caller for `FUN_142169dd0`): the client's village dispatcher `FUN_142150ae0` — reachable only as the
ClientVillage secondary-vtable slot 1 and also the handler of `0x1f` — calls `FUN_142169dd0(village, kind, a, b, c, d)`
at `0x142150cca`, which notifies the kind's frame, and the settlement record attached to that frame (`vtable +0x18` →
`FUN_141769f20`) hands itself to the panel as `+0x2e0`.

What is still missing is the *creation* half: the `0x1f` message that instantiates the `0x9B` record
(`FUN_142150AE0`, dispatch index `kind - 0x0D`, factory table `DAT_14475DC70` at BSS), and the `0x7b` kind `0x5c`
that opens the window and pushes the village frame (`FUN_142169DD0` → `FUN_140401DF0(village + 0x6678, …)`, see
`protocol/16-waypoints.md` and the `Tools/re/out/kingsmarch` notes for the kinds already known — `0x55` recruitment,
`0x56` world map, `0x58` harbour, `0x5b` disenchant, `0x5e` currency exchange, `0x35` current job row).

`0x1f` is a per-kind field patch stream: the message id selects a byte from the one-byte-per-kind jump table
`DAT_14215B764` (`index = id - 0x0D`, `0xFD` = unsupported), and that byte picks the handler in `FUN_142150AE0`. The
handler for the record the panel reads is `case 0x9b`, which routes one entry into one of three keyed maps on the
village (`village + 0xd5f8`, `+0xd610`, `+0xd628`) according to the entry's two selector bytes at `payload + 0x10` and
`+0x11`, keyed by the `i32` at `payload + 0x18`; a `+0x30` sub-object is handed to `FUN_141724EC0(village + 0x8070, …)`
under a guard. Record *contents* also arrive as `0x1e` string events dispatched to each record's vtable slot 3
(`FUN_14176A8E0` for the `0x9B` record, which handles `reroll_recruits`, `super_reroll_recruits` and
`npc_notification_triggered` and can itself emit `0x2ca`). The generic settlement chain is documented in
`Tools/re/out/kingsmarch/cloud/Q1-settlement-message.md` and `Q4-record-layout.md`; what is specific to the Map Runners
window is that the panel's record is this `0x9B` one, not a separate `0x5c` record.
### The mapping entry on `0x1f`

The dispatcher `FUN_142150AE0` (262 pieces after `NightSplit`, `/root/km2/night/decomp/215/FUN_142150ae0.c`) resolves
the kind through the byte table `DAT_14215B764` (`index = kind - 0x0D`, one byte per kind, `0xFD` = unsupported; the
`0x9B` byte is at `0x14215B7F2`) and then patches the village slot for that kind. For the mapping record the whole
branch is 28 lines:

```c
case 0x9b:
    lStack_6278 = FUN_1400f08a0(param_3);                    /* one entry out of the payload */
    if (*(char *)(lStack_6278 + 0x10) == '\0') {             /* flag A clear */
      plVar23 = FUN_14217c600(auStack_42c8, FUN_140db5fa0(stack, arg, &lStack_6278));
      plVar16 = (longlong *)(param_1 + 0xd5f8);
    } else if (*(char *)(lStack_6278 + 0x11) == '\0') {      /* A set, B clear */
      plVar23 = FUN_14217c630(auStack_4288, FUN_140db5fa0(stack, arg, &lStack_6278));
      plVar16 = (longlong *)(param_1 + 0xd610);
    } else {                                                 /* both set */
      plVar23 = FUN_14217c660(auStack_4248, FUN_140db5fa0(stack, arg, &lStack_6278));
      plVar16 = (longlong *)(param_1 + 0xd628);
    }
    FUN_14215ce30(0x142fa6de8, plVar16, (int *)(lStack_6278 + 0x18), plVar23);
    if (FUN_1400f05f0() && !FUN_140130330(lStack_6278 + 0x30))
      FUN_141724ec0((longlong *)(param_1 + 0x8070), lStack_6278 + 0x30, 0);
    break;
```

So one `0x1f` message of kind `0x9B` carries **one mapping entry**, and its shape is [confirmed]:

| offset | type | meaning |
|---|---|---|
| `+0x10` | u8 | first selector: clear routes the entry into `village + 0xd5f8`, set into `+0xd610`/`+0xd628` |
| `+0x11` | u8 | second selector: with `+0x10` set, clear -> `+0xd610`, set -> `+0xd628` |
| `+0x18` | i32 | the entry's key — the map insert `FUN_14215ce30(0x142fa6de8, map, &key, value)` uses this address |
| `+0x30` | object | sub-object handed to `FUN_141724ec0(village + 0x8070, entry + 0x30, 0)` when `FUN_1400f05f0()` is true and `FUN_140130330(entry + 0x30)` is false |

The three maps are separate per-village containers keyed by that `i32`; nothing in the branch copies the entry into the
record's `+0x190` block, so the per-slot data the panel reads (§4) still comes from somewhere else — the `0x1e`
events or the record's own `0x74` message.

`FUN_140db5fa0` is a 2-line pair wrapper (`*out = arg; out[1] = *entry`), and `FUN_14217c600`/`630`/`660` (all the same
shape) wrap that pair in a 64-byte object whose first word is a vtable — `0x1435e4820` for the first branch — with a
self-pointer at `+0x38`. Those are the map's value type, i.e. what the panel iterates over.

The window kind itself is now settled: the client's screen table at `0x142fd89f0` (24-byte entries: UTF-16 name
pointer, name length, `0x7b` kind) lists `KalguurMapRunnersWindow` as kind **`0x5c`**, next to the rest of the
settlement set — `0x54` `KalguurRecombinatorWindow`, `0x55` `KalguurRecruitmentWindow`, `0x56` `KalguurTownMapWindow`,
`0x57` `KalguurMineWindow`, `0x58` `KalguurPortWindow`, `0x59` `KalguurFarmWindow`, `0x5a` `KalguurBlackSmithWindow`,
`0x5b` `KalguurDisenchantingWindow`, `0x5c` `KalguurMapRunnersWindow`, `0x5d` `KalguurFishingWindow`. So the Mapping
panel is the **map runners** window: the server has to send `0x7b` kind `0x5c` for the client to open it, in addition
to the village record itself.

Then, for the buttons:

1. `0x24` on the Kingsmarch map device → the server answers with the village data and the `0x7b` kind that opens the
   town map (`0x56`) and this panel (`0x5c`, `KalguurMapRunnersWindow`).
2. C→S `0x2ca` needs a handler. The opcodes to accept are `0x0c`, `0x0d`, `0x0e` (index + flag), `0x0f`, `0x10`,
   `0x11`, `0x1d`, and `0x04` (byte + count) — the payload is a byte string after the frame.
3. After each state change the server must push the updated village mapping state back (`0x1f`/`0x7b`), because the
   panel repaints only from that snapshot (`FUN_14054de50`), not from its own click results.

### How the panel is handed its data - and therefore the message order `[confirmed]`

`panel + 0x2e0` is not written by the `0x7b` handler. It is written by a per-window setter of the form
`void set_data(window, object) { ... *(longlong *)(window + 0x2e0) = object; ... }` - the mapping window's own setter is
one of that family (`FUN_1404da1c0`, `FUN_140394890`, `FUN_14048c310`, ... all have the same shape, each writing its own
class's data slot; which controller offset holds the mapping window specifically was not pinned - the fan-out hands the
same object to about thirty of them) - and every one of those setters is called from a single fan-out, `FUN_140dba5d0`:

```
140dad190   construction of the Kingsmarch UI controller (sets its vtables at +0x0, +0x220, +0x228, ...)
140dad150   the allocation entry point for that controller (.rdata 0x1431fbf60)
140dba5d0   void FUN_140dba5d0(controller, settlement_object, x)
              if (*(longlong *)(controller + 0x2c8) == settlement_object) { ...dedupe... }
              *(longlong *)(controller + 0x2c8) = settlement_object;        // line 629
              ...
              FUN_140c3e340(*(controller + 0x880), settlement_object);
              ... ~30 sub-windows, +0x880 .. +0xd28 ...
              FUN_1404da1c0(*(controller + 0x8d8), settlement_object);      // line 1468
```

So the panel is fed by the *settlement object itself* arriving at the controller, one fan-out call per sub-window, and the
`0x7b` kind `0x5c` message only shows a window that was already built (`FUN_140d632c0` builds all of them eagerly) and
already holds its data.

**Order therefore:** the settlement must exist on the client before the window is opened - one `0x1f` message of kind
`0x9B` (which is what creates the record and its `+0x190` block, §"The mapping entry on `0x1f`") **must be sent before, or
at least together with, the `0x7b` kind `0x5c`**. Sending `0x7b` alone produces a visible window whose four buttons all
return early (`panel+0x2e0 == 0`), which is exactly the symptom being fixed; sending `0x1f` alone updates a window the
player cannot see. `panel+0x371` is set by the same kind of per-object notification rather than by either message, so a
`0x1f` followed immediately by `0x7b` may still show one disabled refresh cycle - the safe order for the fix is
`0x1f` (record) → `0x1e`/refresh → `0x7b` (open).

Whether the object handed over is the `0x9B` record or the village that owns it is `[candidate]`: the setter family ends
up reading `+0xc9f8`/`+0xca00` (0x18-strided listener list) off the same pointer, which only a village-sized object has,
while the panel's own reads (`+0x190`, `+0x160`, `+0x2a0`) fit the 0x5A8-byte record; both objects carry a `+0x190`
block of the same layout.

## 6. Open questions

- Which payload carries the devices. Kind `0x5c` is answered: `FUN_142169DD0`'s `case 0x5c:` calls
  `FUN_140401DF0(village + 0x6678, payload)`, so the map-runners frame is `village + 0x6678` and the kind is delivered
  by notifying that frame's listener list (each listener's vtable slot `+0x18` receives the payload). The frame slot per
  kind is arbitrary — `0x54`→`+0x61f8`, `0x55`→`+0x6278`, `0x56`→`+0x62f8`, `0x57`→`+0x64f8`, `0x58`→`+0x6378`,
  `0x59`→`+0x6578`, `0x5a`→`+0x65f8`, `0x5b`→`+0x63f8`, `0x5c`→`+0x6678`, `0x5d`→`+0x6478`, `0x5e`→`+0x4f8`,
  `0x5f`→`+0x2078` — so the earlier `+0x6078 + 0x80 * (kind - 0x54)` rule is falsified. Still open: whether the device
  array itself arrives in that payload or in the `0x1e` record events.
- ~~The per-slot field meanings inside the 0x110-byte snapshot.~~ Closed except for the 15 dwords at `+0x64..+0x9c` and
  confirmation of what `FUN_1424c4550` contributes; the reader table in §4 is exhaustive. Settled: the block's layout (§4), that the rows are the
  three 0x18-byte records at `+0xbe` and that they are `Map Device 1..3` (job type 5), that the three qword pairs at
  `+0xa6` are the three boats (job type 6), that `row + 0x00`/`+0x04` are current/required, that `row + 0x14` is the
  pending-reward count, that `row + 0x15`/`+0x16` are the running/pending flags, and that `+0x08`/`+0x0c` are the run's
  duration in hours and its elapsed seconds. Not settled: the 15 dwords at `+0x64..+0x9c` (and confirmation that the 21
  at `+0x10..+0x60` are the `VillageResources` ledger), and what `FUN_1424c4550`'s part of the snapshot (read from
  village `+0x160` and `+0x2a0`) contributes - `+0x160` is a vector of 0xb8-byte records serialized by `FUN_1424c7f80`.
- Whether the server must implement map runs (workers consuming maps, `%Complete`, rewards) before "Open Map" does
  anything, or whether the client is satisfied by the record and the kind `0x5c` message alone.
- The intent of the three no-argument opcodes `0x0f`/`0x10`/`0x11` on the 0x2b8 control `[99]` (and of the second
  control `[100]`'s `0x1d`, see above).
- Settled by §5 "How the panel is handed its data": the record first, then the kind. Remaining detail:
  the panel receives the record through the village frame notification (a listener's vtable slot `+0x18` is
  `FUN_141769f20`, which copies it into `record + 0x2a0` and subscribes), and `0x140dad910` (34418 bytes, reachable only
  as the `.rdata` vtable slot `0x1431fc298`) opens the screens. Open only: whether the client loses one refresh cycle
  between the record and the open, and whether the object fanned out to the windows is the record or the village.
- What the two selector bytes on the `0x9B` entry mean. `+0x10` and `+0x11` route one entry into `village + 0xd5f8`,
  `+0xd610` or `+0xd628` (see §5), and those three maps are read back by code that has not been followed yet. The most
  likely reading is a per-state split (available / running / finished), but nothing in the branch says so.
- What the `+0x30` sub-object is: it is handed to `FUN_141724EC0(village + 0x8070, entry + 0x30, 0)` only when
  `FUN_1400F05F0()` is true and `FUN_140130330(entry + 0x30)` is false, and `village + 0x8070` is a different container
  from the three entry maps.

## 7. Coverage and next steps

**Scanned.** The whole image, statically, for: the `0x7b` screen table (`0x142fd89f0`, 93 rows, name + kind for every
row); the `0x2ca` gadget descriptors (45 descriptors reached from the 40 direct `E8` callers of `FUN_1421a1960`; the
Ghidra count including indirect calls is 152); every `PTR_LAB_14348…` referenced by the map-runners handlers; the
ClientStrings rows behind every id the panel ctor passes (`client.db`, table `ClientStrings`, 7430–7505 read in full).
Decompiled in full: `FUN_14054a2f0` (ctor), `FUN_14054d650`/`d6b0`/`d710`/`d780`/`d7f0`/`d850`/`d8c0`/`d920` (button
handlers), `FUN_14054ec40`/`FUN_14054ea40` (gamepad), `FUN_14054d050`/`d400`/`d120`/`e550` (lifecycle), `FUN_140d632c0`/`FUN_140d9e5c0`/`FUN_140555630`/`FUN_140d95fd0`
(window construction and open path), `FUN_1417690f0` (the snapshot copier).

Read in full as well, after the first draft: the settlement record's ctor `FUN_141767ba0` and event handler
`FUN_14176a8e0`, its wire serializer `FUN_141767f60` and the nested deserializer `FUN_1424c8190` / `FUN_1424c8020`
(which is where §4's 0x110-byte layout comes from), the kind dispatcher `FUN_142169dd0` and its sole caller
`FUN_142150ae0` (the `0x1f`/kind handler), the notify primitive `FUN_140401df0`, the frame subscribe/unsubscribe pair
`FUN_1401a02f0`/`FUN_1401a0260`, and the panel's refresh `FUN_14054de50` and selection setter `FUN_14054e550`.

**Not covered.** The 36 dwords at record `+0x10..+0x9f` are still unnamed, and so is the intent behind the `0x7b` kind
`0x5c` argument `a1`: its two listeners (`FUN_140d6d300`, `FUN_140dba5d0`) were read and neither uses the payload, so
the value has no observable effect in the paths covered here. The four `ClientVillage` mapping API bodies were checked
only for gadget-descriptor references, not read end to end. The huge engine functions the splitter was pointed at
(`0x14194c880`, `0x14006a5f0`, `0x142401e00`, `0x14124b4e0`, `0x141ece040`) are not Kingsmarch code and were abandoned
when the container's OOM killer took the decompiler twice. No runtime observation was made: every statement here is
static, and "unreachable" always means "unreachable along the paths read".

**Contradictions found and resolved.** An earlier draft had the `0x0f` and `0x11` rows swapped (`FUN_14054d7f0` for
`0x0f`, `FUN_14054d850` for `0x11`); the byte-level decode (`0x141777d70` writes `0x0F`, `0x141777cd0` writes `0x11`)
and the handler bodies (which descriptor each handler loads) now agree, and the table above reflects that. The
"decompile the four `ClientVillage` APIs to recover the opcode order" route is falsified and removed.

**Next steps.** (1) Name the fields of the 0x110-byte block: the 36 dwords at `+0x10..+0x9f`, the three qwords at `+0xa6`, the three 0x18-byte records at `+0xbe`, and the six bytes at `+0xa0` — the refresh's formatting code and the record's `0x1e`/`0x1f` handlers are where the names will come from.
(2) Identify the owner chain of the settlement object that holds the mapping devices: every UI reader takes it as
`*(x + 0x460) + 0xc890` / `+0xc9d0` / `+0xcb90`, and at least one constructor initialises the same member as an
intrusive-list sentinel (`FUN_14214b4a0` line 262: `param_1[0x460] = param_1 + 0x461`), so `+0x460` is a list head in
one class and a pointer in another. Until that chain is resolved the writer of the `+0xc9d8` vector stays unknown,
and displacement scanning cannot help: the base is an interior pointer and `FUN_14214b4a0`'s decompiled body does not
carry the displacement.
(3) Only then design the server side: a `0x1f` village record plus a `0x7b` kind
`0x5c` writer, and a C→S `0x2ca` handler for `0x0c`/`0x0d`/`0x0e`/`0x1d`.
