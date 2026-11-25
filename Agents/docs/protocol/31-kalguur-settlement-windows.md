# 31. Kingsmarch: the other settlement windows (Recruitment, Harbour, Rewards)

Companion to [30](30-kalguur-mapping.md), which covers the Mapping window (君峰镇 异界冒险). This page records the
rest of the Kingsmarch settlement UI as the client implements it: the windows opened by S→C `0x7b` kinds `0x55`
(`KalguurRecruitmentWindow`) and `0x58` (`KalguurHarbourWindow`), the Rewards window that Mapping and Shipping
share, and the per-row action handlers that Mining, Smelting, Farming and Disenchanting use. Everything here is
read from the 3.29.3c client (build 327984); none of it is implemented in the Gateway.

The Mapping page's conclusion applies to all of them unchanged: **a settlement window renders from the record the
manager hands it, so the record message must precede the `0x7b` open**, and the window's buttons are inert until
that record exists.

## 1. How a settlement window is given its data

The Kingsmarch UI manager builds every window eagerly in its ctor, and one fan-out call distributes the settlement
object to all of them:

| Address | What |
|---|---|
| `0x140dad190` | manager ctor: sets the vtables at `+0x0`, `+0x220`, `+0x228`, … |
| `0x140dad150` | allocation entry for the manager (`.rdata 0x1431fbf60`, one reference) |
| `0x140dba5d0` | `void FUN_140dba5d0(manager, settlement_object, x)` — dedupes against `manager+0x2c8`, stores it there, then calls ~30 sub-window setters, among them `FUN_1404da1c0(*(manager+0x8d8), object)` at line 1468, which is the setter that writes `window+0x2e0` |
| `0x14054d050` | the Mapping panel's own open path; called with `*(manager+0x618)+0x2e8` |

`FUN_140dba5d0`'s only caller is `0x140dad6c2` inside the ctor, and the ctor's only caller is `0x140dad150`, so the
chain is manager construction → settlement object → every window's `+0x2e0`. `[confirmed]`

The per-window slot that holds the settlement object is **not** uniform, which matters for any generic handler:

| Window | Kind | Object slot | Gate in the button handlers |
|---|---|---|---|
| Mapping `KalguurMapRunnersWindow` | `0x5c` | panel `+0x2e0` (with `+0x371` = data-valid) | `panel+0x2e0 != 0 && panel+0x371 != 0` |
| Recruitment `KalguurRecruitmentWindow` | `0x55` | **manager** `+0x360` | `(manager+0x1e8 >> 0xb & 1) && manager+0x360 != 0` |
| Harbour `KalguurHarbourWindow` | `0x58` | panel `+0x2e0` | `panel+0x2e0 != 0` |
| Smelting / Mining / Farm / Disenchanting | `0x5a` / `0x57` / `0x59` / `0x5b` | panel `+0x6e8` | `panel+0x6e8 != 0 && panel+0xa26 == 0` |

The only writer of `manager+0x360` in the whole image is the manager ctor at `0d6/FUN_140d632c0.c:621`. `[confirmed]`

The `0x7b` frame each kind notifies (from `FUN_142169dd0`, see [30] §5): `0x54`→`+0x61f8`, `0x55`→`+0x6278`,
`0x56`→`+0x62f8`, `0x57`→`+0x64f8`, `0x58`→`+0x6378`, `0x59`→`+0x6578`, `0x5a`→`+0x65f8`, `0x5b`→`+0x63f8`,
`0x5c`→`+0x6678`, `0x5d`→`+0x6478`.

## 2. Recruitment (`0x7b` kind `0x55`)

Panel ctor `0x14050b800` (1701 bytes of decompiled body, `050/FUN_14050b800.c`), title `0x1c8a` = ClientStrings row
7305 `KalguurRecruitmentWindowTitle` "Recruitment". Labels: `0x1c8b` "Hire Workers" (line 354), `0x1c8c` "Current
Workers" (1133), `0x1c8e` "{0}/{1}" (1212), `0x1d55` "Kingsmarch" (1482). Controls `[0x5e]` list, `[0x5f]` reroll,
`[0x60]` super-reroll; handlers are registered with `FUN_14020f250(param_1[0x5f] + 0x2a0, param_1, &handler)`, so
their context is the **manager**, not the panel.

| Opcode | Descriptor | Sender | Payload | Meaning |
|---|---|---|---|---|
| `0x00` | `0x14348df08` | `051/FUN_140510390.c` (sends 0), `051/FUN_140510400.c` (sends 1) | 1 byte mode | reroll recruits: 0 normal, 1 high quality. The same descriptor is what the `0x9B` record emits for the `reroll_recruits` / `super_reroll_recruits` events |
| `0x01` | `0x14348e358` | `051/FUN_1405115a0.c` | 1 byte index into the 0xb0-byte worker list | **fire worker** — confirmed by the RTTI name `KalguurRecruitmentWindow::FireWorkerClickedHandler(KalguurWorkerOption*)` |
| `0x02` | `0x14348e1a8` | `050/FUN_14050fe20.c`, `051/FUN_140510270.c` | 1 byte index into the 0xb8-byte recruit list at `*(settlement+0x160)`, taken from the row's `+0x440` | **hire recruit**, sent on Ctrl+click (`GetKeyState(0x11)`) |
| `0x03` | `0x14348db60` | `050/FUN_140508990.c` | 1 byte `*(row+0x2b0)` | row select (shared descriptor, see §6) |
| `0x16`/`0x17` | — | — | — | not recruitment: these belong to the Rewards window (§4) |

Costs and labels come from `client.db`: `VillageSharedConstants.QuotaRerollCost` 15000,
`RecruitSuperRerollCostMultiplier` 8, and the tooltip `0x1c96` "Reroll Recruits\nCost: {0} Gold". The gold shown is
the player's own (`0x1c98` "Gold:" = `KalguurPersonalGold`), so this window does not depend on the treasury at
`village+0xC40C`. `[confirmed]`

## 3. Harbour (`0x7b` kind `0x58`)

Panel ctor `0x14057c2b0` (`057/FUN_14057c2b0.c`, 1393 lines), title `0x1ca3` = row 7330
`KalguurHarbourWindowTitle` "Harbour". Sends from `panel+0x2e0`. `[confirmed]`

| Opcode | Descriptor | Sender | Payload | Meaning |
|---|---|---|---|---|
| `0x03` | `0x14348db60` | `057/FUN_140581c40.c` | 1 byte row id + dword | row select |
| `0x06` | `0x14348e790` | `057/FUN_140581610.c` | two dwords | set shipment (amount + resource) `[candidate]` |
| `0x07` | `0x14348d940` | `057/FUN_140581af0.c` | 1 byte `*(row+0x3e8)` | per-row action |
| `0x08` | `0x14348dd98` | `057/FUN_140581a20.c` | none | no-payload action (also hides control `+0x378`) |
| `0x09` | `0x14348dd68` | `057/FUN_140581a90.c` | none | the pair of `0x08`; `[candidate]` send/clear |
| `0x0a` | `0x14348e258` | `058/FUN_140581960.c` | — | `[unknown]` |
| `0x0b` | `0x14348dca8` | `058/FUN_140580e10.c` | string + int (`FUN_1422b78b0(&out, *(int*)(row+0x418), &value)` after a table lookup) | choose destination port |
| `0x12` | `0x14348e7f0` | `057/FUN_140581b70.c` | 1 byte `*(row+0x3e8)` | per-row action |
| `0x00` | `0x14348b4c0` | `0x1405871b0` | — | generic family, not a `0x14348d*/0x14348e*` descriptor |

Shipping data lives in `VillageShippingPorts` (7 rows: Kingsmarch ×1.0 distance 1, Riben Fell ×0.75 140, Ngakanu
×0.75 186, Pondium ×1.0 870, Te Onui ×1.0 939, Kalguur ×1.25 2317, Moti Aro ×1.25 1143) and
`VillageShippingFailures`; the value constants are `ExpectedShipmentValue` 150, `MinShipmentValue` 50000,
`MaxShipmentValue` 50000000, `ExpectedCrewValue` 3.

## 4. The Rewards window — shared by Mapping and Shipping

Ctor `051/FUN_140512680.c` (1032 lines), title `0x1ccd` = row 7372 `KalguurRewardsWindowTitle` "Rewards". It has no
`0x7b` kind of its own in the screen table, so it is opened by the client as part of the reward flow (Mapping's
`0x1d` = `OpenMappingRewards`). It carries two actions: `[confirmed]`

| Opcode | Descriptor | Sender | Payload | Meaning |
|---|---|---|---|---|
| `0x16` | `0x14348dcd8` | `051/FUN_140515430.c` | one dword `0`; afterwards sets `panel+0x390 = 1` | **take the rewards** |
| `0x17` | `0x14348da30` | `051/FUN_1405154a0.c` | none | **discard all remaining rewards** |

`0x17` sends at once when Ctrl is held (`GetKeyState(0x11)`), otherwise it opens a confirmation dialog
`FUN_1401a0ab0(..., title 0xe49 "Warning", text, 0x3e "OK", 0x72 "Cancel", ...)`. The text is selected by
`FUN_142252050(*(panel+0x380))`: **`== 6` → id `0x1cd2` = row 7377 `KalguurShippingRewardsDisgardAllWarning`**,
otherwise **id `0x1de6` = row 7653 `KalguurMappingRewardsDisgardAllWarning`**. So the window is shared, and the
reward kind is the `VillageJobTypes` value of `panel+0x380` (5 = Mapping, 6 = Shipping).

## 5. Mining, Smelting, Farming, Disenchanting — per-row handlers

These windows create their buttons per row at run time, so **their ctors contain no gadget and no
`FUN_1421a1960` call** — the senders live in neighbouring handlers and send from the record pointer, not from
`+0x2e0`:

| Address | Window | Opcode | Descriptor | Payload / gate |
|---|---|---|---|---|
| `053/FUN_14053cfd0.c` | Smelting | `0x12` | `0x14348d970` | 1 byte `*(panel+0x6e0)` (selected row); gates `*(panel+0x6e8) != 0 && *(panel+0xa26) == 0`, a `std::map` walk at `*(panel+0x5c8)+0x188`, `acStack_46[*(panel+0x6e0)*0x18] != 0` (that slot has pending rewards), `*(char*)(panel+0x730) != 0`, `(*(uchar*)(panel+0x6f8) - 2) < 2`; else falls back to `FUN_14030da60` |
| `053/FUN_14053d100.c` | Smelting | `0x12` | `0x14348d970` | identical body |
| `053/FUN_140537e80.c` | Smelting | `0x05` | `0x14348ded0` | none, from `*(panel+0x340)` |

So the per-window panel layout for this family is: `+0x6e8` record pointer, `+0x6e0` selected row byte, `+0xa26`
"busy" flag, `+0x5c8` container (`+0x188` = `std::map<uint, row>`), `+0x6f8` small enum, `+0x730` boolean.
`[confirmed]`

## 6. Opcodes are not identities — descriptors are

Two facts make a naive opcode table useless on the server:

- `0x03` (descriptor `0x14348db60`, the shared row-select) is sent by Recruitment, the worker editor, the town map
  and the Harbour.
- `0x12` exists twice with different payloads: `0x14348d970` in Smelting (1 byte row) and `0x14348e7f0` in the
  Harbour (1 byte `row+0x3e8`).

| Bucket | Window | Senders (opcode) |
|---|---|---|
| `050`/`051` | Recruitment | `0x03`, `0x02`×2, `0x00`×2, `0x01` |
| `051` | Rewards | `0x16`, `0x17` |
| `052` | Worker editor / town map | `0x03`×4 |
| `053` | Smelting | `0x05`, `0x12`×2 |
| `054` | Mapping | `0x04`, `0x0c`, `0x0d`, `0x0e`×3, `0x0f`, `0x10`, `0x11`, `0x1d` |
| `057`/`058` | Harbour | `0x03`, `0x06`, `0x07`×2, `0x08`, `0x09`, `0x0a`, `0x0b`, `0x12` |

The server must therefore route on the (descriptor, opcode) pair, or on the window that produced the message — the
descriptor address is the only stable identity. `[confirmed]`

`0x2ca` itself is **not** Kingsmarch-specific `[candidate]`: 139 senders of `FUN_1421a1960` exist, and buckets
`0x059`–`0x066` contain many of them while containing no `Kalguur`/`Village` string at all; two descriptors from
different windows share one writer (`0x14348b538` and `0x14348e1a8` both use `0x14173fee0`), and no writer contains a
literal `0x2ca` immediate, so the id is set by a shared message base. Read `0x2ca` as "generic UI action" until that
base is identified.

## 7. Attaching names to the functions

No CompleteObjectLocators exist on this binary, but the RTTI type-name and assert strings survive, and they name real
methods. Scan every section for printable ASCII containing `Kalguur` (`pat = rb'[ -~]{8,220}'`); the useful hits:

- `KalguurRecruitmentWindow::FireWorkerClickedHandler(KalguurWorkerOption*)`,
  `::UpdateCurrentWorkers()`, `::UpdateHireableWorkers()`, `::EnsureGamepadSelectionIsPopulated()`
- `KalguurWorkerEditor::UpdateWorkers()`
- `KalguurShippingWindow::AbandonHandler(KalguurShipFrame*)`,
  `ShipingDestinationPin::UpdatePortQuota(ShippingPortQuota, uint)`,
  `KalguurShipFrame::HasAnyCrew()`, `KalguurShipRiskPopup::SetInfoLabel(unsigned int)`
- `KalguurRewardsWindow::DisgardAllButtonClickedHandler(Control*)` (sic, "Disgard")
- inventory views: `InventoryViewKalguurDisenchanting`, `InventoryViewKalguurRemoteVendor`

## 8. What the server has to do

For each settlement window, in this order:

1. send the settlement record (`0x1f`, kind `0x9B`) so the manager hands every window its object — without it the
   panel gates stay closed and every button is a no-op;
2. send any `0x1e` string event the client needs to refresh (`reroll_recruits`, `super_reroll_recruits`,
   `check_reward_icon_visibility`, `npc_notification_triggered`, `village_imgui` are the only five in the image);
3. send `0x7b` with the kind of the window (`0x55` recruitment, `0x58` harbour, `0x5c` mapping, …);
4. answer `0x2ca` by (descriptor, opcode, payload) — reroll (`0x00`), hire (`0x02`), fire (`0x01`), shipment
   (`0x06`), port (`0x0b`), take rewards (`0x16`), discard all (`0x17`), and the per-row `0x12`/`0x05` of the
   production windows once their payloads are confirmed.

No `0x2ca` handler exists in the Gateway today, and no Kingsmarch settlement module exists either; the screen writers
to copy are `Modules/Core/World/Portals/PortalsFeature.cs:136`, `Modules/Core/Item/Bench/BenchModule.cs:89`,
`Modules/Core/Game/Item/Stash.cs:76`/`:93` and `Modules/Core/Content/Delve/DelveModule.cs:146`.

## 9. Coverage and open questions

Covered `[confirmed]`: the data path (manager → `FUN_140dba5d0` → each window's object slot), the kind → frame
table, the Recruitment, Harbour and Rewards opcode sets with payloads, the Smelting row handler, the descriptor
identity rule, and the recovered C++ names.

Open:

- the exact payload semantics of Harbour `0x06` (two dwords), `0x07`/`0x12` (row byte), `0x08`/`0x09` and `0x0a`;
- Recruitment `0x01`'s index list (0xb0-byte elements) versus the 0xb8-byte recruit list;
- Mining, Farming and Disenchanting row handlers (their sends exist in the `054`/`056` buckets but have not been
  attributed row by row);
- whether `0x2ca` is one global message id for all UI actions, and which base class writes `0x2ca` at `+8`;
- `village+0xC40C` (treasury gold) has no static writer anywhere in 108,841 decompiled bodies apart from the
  populator's zeroing at `214/FUN_14214b4a0.c:1263`, so it needs a live write-watch;
- the `+0x160` recruit-list writer and the `0x74` record prerequisites are still unknown (both need hooks).
