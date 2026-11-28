# Settlers of Kalguur / Kingsmarch (3.29.3c)

Native 3.29.3c protocol and persistence reference for Kingsmarch (`KalguuranSettlersLeague`, `Rulesets` row `155` / `0x9b` `VillageRules`), map ore encounters, boss arenas, and the dual-mode Recombinator.
Code: `src/GameServer.Core/Protocol/Instance/InstanceVillage.cs`, `src/GameServer.Gateway/Saves/VillageStore.cs`, `Modules/Core/Content/Settlers/`.

## 1. Ruleset Channel & OpenScreen (`0x001f`, `0x001e`, `0x007b`)

- **S→C `0x001f` (AttachRuleset):** `u16be 0x001f, u8 0x9b, u32be 0`. Sent before `0x0021` in Kingsmarch, Karui Shores (`2_11_endgame_town`), and hideouts so town panels attach natively.
- **S→C `0x001e` (RulesetEvent, reader `0x1421a44a0`):** `u16be 0x001e, u32be entityId, u16be byteLen, utf8[byteLen]`. `ClientVillage::OnRulesetEvent` (`0x14176a8e0`) handles `village_imgui`, `reroll_recruits`, `super_reroll_recruits`, `check_reward_icon_visibility`, and `npc_notification_triggered`.
- **S→C `0x007b` (OpenScreen, screen table `0x142fcd700` / `0x14216bb74`):**

| Kind | Screen | Params |
|---|---|---|
| `0x54` | `KalguurRecombinatorWindow` | none |
| `0x55` | `KalguurRecruitmentWindow` | none |
| `0x56` | `KalguurTownMapWindow` | none |
| `0x57` | `KalguurMineWindow` | `p1` = ore/mine index |
| `0x58` | `KalguurPortWindow` | none |
| `0x59` | `KalguurFarmWindow` | `p1` = farm plot index (`0..5`) |
| `0x5a` | `KalguurBlackSmithWindow` | `p1` = bar/smelter index |
| `0x5b` | `KalguurDisenchantingWindow` | none |
| `0x5c` | `KalguurMapRunnersWindow` | `p1` = map device index (`0..2`) |
| `0x5d` | `KalguurFishingWindow` | 3.29 no-op |
| `0x5e` | `CurrencyExchangeWindow` | owned by `CurrencyExchange` module |

## 2. Ruleset Messages (`S→C 0x02c9` / `C→S 0x02ca`)

Envelope: `u16be opcode, u8 0x9b, compact_uint len, u8[len]`. All integer fields inside the payload are **little-endian**.

- **S→C `0x02c9` (`0x141767f60`, subtags `0x00..0x0d`):**
  - `0x00` `FullWorkerList`: `u8 count`, `count * HiredWorker` (`u32 firstWordHash, u32 lastWordHash, u16 wagePerHour, u8 skillCount, skillCount*(u8 jobType, u8 rank), u8 assignedJobRow`).
  - `0x01` `RecruitPool`: `u8 flag, u8 count, count * { WorkerCore, u32 hireCost }`.
  - `0x02` `ShipUpdate`: `u8 shipIdx, u8 portRow, u8 failureRow, u32[16] cargoExports`.
  - `0x03` `PortQuotas`: `u8 portIdx (0..5), 3 * u64` packed as `((exportRow & 0xF) << 56) | ((required & 0x0FFFFFFF) << 28) | (delivered & 0x0FFFFFFF)` (swapped relative to client memory at `0x1417685b0`).
  - `0x04` `FullVillageState`: `u32[21] resources`, `u32[15] productionProgress`, `u32 goldInTreasury`, `u32 goldBurnRemainder`, `u8[6] farmPlotCrops (21=empty)`, `3*(u32 elapsed, u32 total)` ship timers, `3*MapDeviceState`, `(u32, u32)` disenchant timer, `u8 nibbleLo, u8 nibbleHi`, `u64 stateTimestamp`, `u8 shipKidnapSlot, u8 shipKidnapFlag, u8 mapKidnapSlot, u8 mapKidnapFlag`, trailing `u64 serverNowSeconds`.
  - `0x05` `AddWorker`, `0x06` `RemoveWorker`, `0x07` `SetWorkerJob`, `0x08` `ActiveRewardNotice`, `0x09` `ShippingRewardItems`, `0x0a` `MappingRewardItems`, `0x0b`/`0x0c` toasts (`u8 a, u8 b, u8 c`), `0x0d` `MapDeviceCurrentMapName` (`u8 idx, u32 byteLen, utf8`).
- **C→S `0x02ca` (`0x1421a1960`, subtags `0x00..0x1d`, `0x1a` unused):** `0x00` RerollRecruits, `0x01` HireRecruit, `0x02` FireWorker, `0x03` AssignWorker, `0x04` AssignFarm, `0x05` BuyUpgrade, `0x06` SendShipment (68B), `0x07` CancelShipment, `0x08..0x0a` Rescue/Ransom/Abandon commandeered ship, `0x0b` RerollPortQuota, `0x0c..0x0e` Start/Stop/Suspend mapping, `0x0f..0x11` Rescue/Ransom/Abandon kidnapped mappers, `0x12..0x17` Open/Take/TakeAll/Discard rewards, `0x18`/`0x19` Deposit/Withdraw gold, `0x1b`/`0x1c` NPC notification acks, `0x1d` AckFailedMapRun.

## 3. Recombinator & Inventories

- Inventories: `87` (`KalguurDisenchanting1`, 12×10), `88` (`KalguurMapping1`, 12×3), `92` (`RecombinatorInput`, 8×4), `93` (`RecombinatorOutput`, 4×4 single-item remove-only).
- **C→S `0x028f`:** `u16be 0x028f, u8 reserved, u8 n0, u8[n0] slot0Mods, u8 n1, u8[n1] slot1Mods, u8 mode, u8 execute`. Client `0x141d6c8f0` / `0x141d6d700`: `mode == 0` is **Predictable** (selected mods required), `mode != 0` is **Unpredictable** (no selected mods).
- **S→C `0x0293` (7B):** `u16be 0x0293, u8 mode, u32be chanceBasisPoints` (`chanceBasisPoints / 100.0`%).
- **S→C `0x0291` (5B):** `u16be 0x0291, u8 successBool, u16le errorOrStatusCode`.
- **C→S `0x010b` (9B):** `u16be 0x010b, u32be itemId, u8 sourceFlag, u8 intoRecombinator, u8 isOutputInv93`.

## 4. Hub Objects, Persistence & Waypoint Settings

- `kingsmarch.arm` (offset `(138, 138)`, code `0`) spawns all 349 3.29 buildings, ships, farms, map devices, noticeboards, recombinator, disenchanting stations, and workers natively via `0x02bb` with `StateMachine` updates (`0x02bc`).
- Karui Shores (`2_11_endgame_town`, `camp.arm` L1147-1148) spawns `VillageMayorTown` `(686, 759)` and `VillageFaustusTown` `(686, 746, Model: Faustus.ao)`; `NpcScripts.cs` implements `GoToKingsmarch()`.
- Persistence: 10 `STRICT` league+account tables in `characters.db` (`sql/updates/pending_characters/settlers_village.sql`, `VillageStore.cs`).
- Waypoint tunables (`Modules/Core/Content/Settlers/settings.json`): 16 settings defaulting to 3.29 retail (`1.0` multipliers and `offlineCatchupEnabled = true`).
