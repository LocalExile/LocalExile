# Kingsmarch: the Atlas Runner mapping device

The mapping device is the third Kingsmarch production line (after farming and shipping): the town sends hired Atlas
Runners into Atlas maps on their own, and the town collects what they bring back while they are away. The panel the
player drives it with is the client's `KalguurMapRunnersWindow` (screen kind `0x5c`).

This file records what the client actually does (read out of the 3.29.3c client with IDA and out of the client's own
data tables), and the design we implement on top of it. Everything that is a design decision rather than a client
finding is marked **calibration**; the client findings are marked **client**.

## 1. What the client does (findings)

* **client** The screen table at `0x142fd89f0` (24-byte entries: name pointer, length, kind) is indexed by panel kind.
  `0x5c` is `KalguurMapRunnersWindow`. The village panels are `0x54` recombinator, `0x55` recruitment, `0x56` town map,
  `0x57` mine, `0x58` port, `0x59` farm, `0x5a` blacksmith, `0x5b` disenchanting, `0x5d` fishing, `0x5e` currency
  exchange. (The table in `settlers.md` had these wrong.)
* **client** The client's own action lambdas (`ClientVillage@Rulesets@Game`, 29 of them) are the buttons these panels
  send. The mapping ones are `StartMapping`, `InterruptMapping`, `SetPauseMappingAfterCurrentMap(bool)`,
  `RescueCapturedMappers`, `RansomCapturedMappers`, `AbandonCapturedMappers`, `AcknowledgeLostMapper`,
  `OpenMappingRewards`, `TakeReward`, `TakeAllRewards`, `HireWorker`, `FireWorker`, `RerollRecruits`, `AssignWorker`.
  Every button goes out as C→S `0x2ca`: `u16 id | u8 kind | compact-int length | blob`, and the blob starts with the
  action byte. Only `0x12` ("pause mapping after this map") is read off a real click so far; the rest of the ids are
  still open (see §8).
* **client** The panel's own wording (`data/tables/ClientStrings.json`, HASH32 keys):
  * `KalguurMapRunnersInfoExpectedCompletionPercent` — "Expected Map Completion: {0}%"
  * `KalguurMapRunnersInfoExpectedCompletionTime` — "Expected Map Completion Time: {0}"
  * `KalguurMapRunnersInfoChanceOfRunnnerDeath` — "{0}% chance for an Atlas Runner to be permanently killed in this Map"
  * `KalguurMappingMapCompletionPercent` / `KalguurMappingMapCompletionTime` / `KalguurMappingMapCompleteHover`
  * `KalguurWorkersRequiredForEstimates` — "Assign at least one worker to view estimates" (the estimates need workers)
  * `KalguurMappingStartButton` "Open Map", `KalguurMappingImmediateStopButton` "Exit Map",
    `KalguurMappingDelayedStopButton` "Suspend Mapping", `KalguurMappingUndoDelayedStopButton` "Resume Mapping"
  * `KalguurMappingWorkersKidnapped`, `KalguurJobWorkersLockedErrorMappersKidnapped`,
    `KalguurMappingLockedByHostage` — a kidnapping blocks *other* map devices too
  * `KalguurMappingLostWorker` — "One of your Atlas Runners has died in the Atlas."
  * `KalguurMappingRansomButton` / `KalguurMappingRescueWorkersButton` / `KalguurMappingAbandonWorkersButton`
  * `KalguurWorkersTreasuryGoldRequiredError` — "The Treasury is out of Gold"
  * `KalguurSkillRank` "Rank {0}", `KalguurWorkerLevel` "Level: {0}", `KalguurWorkerWage` "Wage:",
    `KalguurRecruitInitialCost` "Cost:", `KalguurRefreshRecruitsHover` "Reroll Recruits\nCost: {0} Gold",
    `KalguurFireWorkerWarning`, `KalguurHireRecruitErrorMaxWorkersReached`, `KalguurHireRecruitErrorInsufficientGold`
* **client** `VillageMappingPerTier.json` has one row per tier (`Id` 1..17 = T1..T17) and three columns:
  * `__col1` 2000, 2059, 2117, 2173, 2227, 2660, 2726, 2790, 2852, 2912, 3600, 3738, 3872, 4350, 4730, 5100, 5880
    → **the team power a tier asks for**, in thousandths (T1 = 2.00 … T17 = 5.88).
  * `__col2` 25, 30, …, 100, 125 → **base time per map**, in half-minute ticks per power point.
  * `__col3` 25, 40, 55, …, 260, 300 → **base death chance**, in hundredths of a percent (T1 = 0.25% … T17 = 3.00%).
  The jumps in `__col1` (T6, T11, T14) are the tier steps where the game expects a bigger team.
* **client** `VillageJobTypes.json`: the `Mapping` job is `MaxWorkersStat 20726` (`VillageBaseStats` row 20726 = **6**
  workers per device) and `JobSpeedStat 20699`. `VillageBaseStats` row 20728 = 90 is the town population limit.
  `VillageJobTypes` also has the two "absent" jobs, `LostAtSea` and `LostMapping` ("Trapped in the Atlas").
* **client** `VillageSharedConstants.json`: `RansomCost = 50000` (the ransom the player can pay),
  `RecruitSuperRerollCostMultiplier = 8`. `VillageRecruitBalancePerTier.json` has one flat `__col1 = 2000` for every
  tier — the base price of a recruit pool refresh. `VillageJobSkillLevels.json` maps job level 0..5 to character levels
  12/18/27/37/49/63. `VillageMappingBonusStats.json` lists the stat ids a mapping run can grant (11713–11721 at 15%,
  6957 and 14058 at 30%).
* **client** `metadata/statdescriptions/village_stat_descriptions.txt` (45 village stats, 11 languages) gives the exact
  wording of the village stats, and with it the shape of the feature:
  * `village_number_of_map_devices` = **3** — "Map Devices have {0} Portal(s)" (`village_max_mappers_per_device` = 6)
    and `VillageJobs` has three `Mapping` rows (`StatValue` 1, 2, 3). **The town has three mapping devices**, one job
    row each; the panel is per device. The town-map art confirms it (`VillageMapMapDevice1/2/3`).
  * `village_can_run_maps_up_to_tier` = 17, described in four bands:
    "Atlas Runners can run **White** Maps" (tiers 1–5) → "White and **Yellow**" (6–10) → "White, Yellow and **Red**"
    (11–16) → "White, Yellow, Red and **Purple**" (17). **Map rarity is a tier band** (white T1–5, yellow T6–10,
    red T11–16, purple T17) and the device's tier stat decides which bands it may run.
  * `village_worker_generation_rank` = the recruit generation rank; it is what unlocks the specialties — ranks 1–2 know
    Mining/Shipping, rank 7 adds "**Map Running**" — and it also caps the job specialty rank (up to 9).
    So a runner's specialty rank runs 1..9, and the Map Running specialty itself only appears from generation rank 7.
  * `village_maps_have_mod_effect_+%` — the town can make explicit modifiers on a map bite harder.
  * `village_mapping_speed_+%`, `village_number_of_recruits` = 6, `village_recruit_cost_+%`,
    `village_can_mega_reroll` (the "better reroll"), `village_max_number_of_workers` = 90.
  * `village_tier16_maps_have_eldritch_or_maven_influence` — from tier 16 a map can carry an Eldritch or a Maven
    influence, which is the client's own "special content" flag.
* **client** The panel's art (`art/uiimages1.txt`, 358 village entries) names the widgets: `MapRunningBG`,
  `MapRunningButtonBG`, `MapRunningDivider`, `MapRunningTimerBG`, `RunFailedPopup`, `CollectRewardGlowFrame`,
  `CollectMapRewardsIcon` and the three run-state frames `ActiveRunFrame` / `CompletedRunFrame` /
  `HalfCompletedRunFrame` (a run in flight, one finished, one half finished). The recruitment book is
  `VillageHireFireWorkerBG` with the two row types `VillageWorkerInfoSlot` / `VillageCurrentWorkerInfoSlot`.
* **client** `Content.ggpk` holds **no** village widget tree — all 124 `.ui` files in the archive were enumerated and
  none is a village panel. The panel is built inside `Game.patched.exe`, so the widget ids and the exact formula code
  can only come from the executable.
* **client** Nothing in the client reads the player's Atlas passives for these numbers. The estimate is shown only once
  workers are assigned (`KalguurWorkersRequiredForEstimates`), so the estimate is computed from the worker's own rank
  and the map's own tier/rarity/affixes.

### 1.1 The village in memory, read live (Cheat Engine, 2026-11-22)

With the client standing in Kingsmarch (`area KalguuranSettlersLeague`, area hash `36691`), the settlement object
is found by its vtable: the client's `ClientVillage` vtable sits at `Game.patched.exe + 0x346cc18`, so an AOB scan
for those eight bytes in writable memory has exactly one hit, and `readQword(hit) == base + 0x346cc18` confirms it.

| Offset | What it is |
|---|---|
| `+0x0000` | vtable (`base + 0x346cc18`) |
| `+0xc40c` | u32 treasury gold (the constructor zeroes it; no message writes it yet) |
| `+0xcfc8 / +0xcfd0 / +0xcfd8` | the record vector: begin / end / capacity end (an empty vector reads as three nulls) |
| `+0xd000` | a small vector of three handles, next to the numbers 7 and 8 |
| `+0xd040` | u32 `0xa7` and `0xff` |

The town record is `0x5a8` bytes: `+0x008` u32 kind `0x9b`, `+0x010` a back-pointer to the `ClientVillage`
(an identity check worth using when hunting for it), `+0x198` 36 u32s holding `1001…1036` — the `VillageJobs` rows the
`0x2c9` state frame carried — and `+0x228` six bytes of `15`. Everything from `+0x300` to `+0x5a0` is zero in a town
with no mapping running: **there is no twelve-map queue on the record**, so the mapping state is either built only
when a map is opened (`StartMapping`) or lives on the `ClientVillage` past `+0xd040`.

## 2. Domain model

Everything the town owns about mapping, in one file per player (one account), saved in `data/kingsmarch-mapping.json`.

| Thing | Fields |
|---|---|
| `MappingDeviceState` | `Id` (1..3), `Level` (1..4), `Queue` (≤ 12 maps), `Workers` (≤ 6 runner ids), `SuspendAfterCurrent`, `Current` (the run in flight), `Rewards`, `Kidnapped`, `KidnappedRunnerId`, `KidnapDeadline` |
| `MappingQueueEntry` | `MapTypeHash`, `Name`, `Tier` (1..17), `Rarity` (0 white, 1 yellow, 2 red, 3 purple), `AffixCount`, `Special` ("" / Boss / Guardian / Eldritch / Maven / Season), `Scarabs` |
| `MappingRunner` | `Id`, `Name`, `Class` (job), `Level` (1..5), `Rank` (0 = no Map Running specialty, else 1..9), `Wage` (gold/hour), `Dead`, `DeviceId` |
| `MappingRun` | `Entry`, `StartedAt`, `EndsAt`, `Completion`, `Minutes`, `DeathChance` |
| `MappingReward` | `DeviceId`, the map it came from, `Completion`, the drops, the gold, when it was won, `Taken` |
| `MappingRecruit` | `Name`, `Class`, `Level`, `Rank`, `Wage`, `Cost` (what hiring this recruit costs) |

**The team power** of a device is the sum of the *mapping specialty ranks* of the runners assigned to it, each rank
counting as `min(Rank, 5) / 5`, so six rank-5 runners are 6.00 power points and the T17 requirement (5.88) is just
short of a full team. The client's specialty rank goes up to 9, but the power scale tops out at 5: ranks 6–9 are the
higher job specialties of the other lines and give no extra mapping power. A runner without the specialty
(`Rank == 0`) contributes nothing and cannot be put on a device.

**The device level** unlocks the tiers a device may run and adds a little power:

| Device level | Tiers it may run | Map rarity it unlocks | Power multiplier |
|---|---|---|---|
| 1 | T1–T5 | white | ×1.00 |
| 2 | T1–T10 | white, yellow | ×1.03 |
| 3 | T1–T16 | white, yellow, red | ×1.06 |
| 4 | T1–T17 | white, yellow, red, purple | ×1.09 |

A town owns three devices (`village_number_of_map_devices` = 3); the town level buys them.

## 3. The three formulas

All of these are **calibration**: the client shows the same three numbers but we could not read the code that
computes them, so these are our numbers, fitted to the client's tables and to the balance target below.

```
power        = Σ over assigned, alive runners of (min(Rank, 5) / 5)    # 0 … 6
powerEff     = power × (1 + 0.03 × (deviceLevel − 1))
requirement  = VillageMappingPerTier[tier].col1 / 1000              # 2.00 … 5.88
rarityMult   = white 1.00 | yellow 1.01 | red 1.02 | purple 1.03
affixMult    = 1 + 0.01 × min(affixCount, 5)

completion%  = clamp(100 × powerEff / (requirement × rarityMult × affixMult), 0, 100)
minutes/map  = (col2(tier) / powerEff) × 0.5 × (100 / completion%)   # col2 = 25 … 125, capped at 1440
death%       = clamp(base + affixPenalty + completionPenalty + teamPenalty − skillRelief, 1.0, 100)
   base              = col3(tier) / 100                               # a percent: 0.25 … 3.00
   affixPenalty      = 0.10 × min(affixCount, 5)
   completionPenalty = 0.05 × (100 − completion%)
   teamPenalty       = 0.20 × max(0, 6 − assignedRunners)
   skillRelief       = min(1.5, 0.05 × Σ (Rank − 1))
```

The death chance has a hard floor of 1%: it is never 0, and a run is never risk-free.

### Balance check

| Team | Device | Map | Completion | Minutes | Death |
|---|---|---|---|---|---|
| 6 × rank 5 (6.00 → 6.54) | L4 | T17 purple, 0 affix | **100%** | 9.6 | 1.8% |
| 6 × rank 5 | L4 | T17 purple, 2 affix | **100%** | 9.6 | 2.0% |
| 6 × rank 5 | L4 | T17 purple, 5 affix | **100%** | 9.6 | 2.3% |
| 5 × rank 5 (5.45) | L4 | T17 purple | 90% | 12.7 | 2.7% |
| 3 × rank 3 (1.85) | L2 | T5 white | 83% | 14.6 | 2.0% |
| 1 × rank 1 (0.20) | L1 | T1 white | 10% | 625 | 5.8% |

The balance target holds: a full team of six rank-5 runners clears T17 at 100% completion in the common cases, and a
twelve-map queue takes about an hour (12 maps of T10: 12 × 5.4 min ≈ 64 min; a queue that reaches T16 and T17 takes
about 84 min). A team far below the tier's requirement does not fail — it simply takes a very long time (the map time
is capped at 24 hours), which is the pressure that makes better runners worth their wage.

## 4. Rewards

A finished map pays out what the map would have dropped, scaled by how much of it was completed.

* The simulated monster level is the client's own rule: `tier + 67` (so T1 = 68, T16 = 83) — `MapDevice.MapLevel`.
* The drop roll goes through the existing `ItemDrops` engine with that level; the counts are then multiplied by
  `completion% / 100`, so a 60% map pays 60% of a full map's loot.
* **No player bonuses are read.** The roll is made against the map and the team only: no Atlas passive presets, no
  completion bonus, no voidstones, no character stats. The player's own mapping bonuses never enter the simulation.
* **Item level caps.** A few bases may not roll above their own cap; cluster jewels cap at **83** (`ClusterJewels`
  otherwise defaults to 84). The cap is applied after the roll, per base type.
* **Bonus stats.** `VillageMappingBonusStats` is rolled per finished map: each listed stat id at its listed chance
  (15% for 11713–11721, 30% for 6957 and 14058) is written into the reward.
* **Special content.** `Boss` adds one affix to the death and time maths and pays a boss fragment; `Guardian` adds
  10% to the time; `Season` is the hook a league module uses to add its own items.
* **Gold.** A map pays `100 + 150 × tier` gold × `completion% / 100` (with ±20% spread) into the town treasury, so
  mapping is what funds the town.

## 5. Economy

* The treasury is the town's `KingsmarchPort` treasury (`SavedPort.Gold`); the mapping line never keeps a second purse.
* **Wages** are charged per hour per assigned runner, from the treasury only. A treasury that cannot pay accrues the
  debt (`KingsmarchPort.PayWages`) and the panel says "The Treasury is out of Gold".
* **Recruits.** The recruitment panel offers three candidates. `RerollRecruits` costs `2000` gold
  (`VillageRecruitBalancePerTier`), a super reroll `2000 × 8 = 16000` (`RecruitSuperRerollCostMultiplier`). Hiring a
  candidate costs `2000 + 500 × Rank` gold, and a candidate with the Atlas Runner specialty costs 1.5× that — the
  specialty is what makes a runner worth its wage. **calibration** for the flat table.
* **Wages.** A runner's wage is `20 + 10 × Level + 15 × Rank` gold per hour; the specialty carries the premium.
* **Ransom.** `50000` gold (`VillageSharedConstants.RansomCost`) buys the kidnapped runners back.

## 6. The two things that interrupt a mapping town

**A runner dies.** The run's death chance is rolled when the map finishes. If it hits, one assigned runner dies
permanently (`KalguurMappingLostWorker`), the device stops, and the player acknowledges it
(`AcknowledgeLostMapper`) before the queue continues. A dead runner is never reused.

**Bandits kidnap a runner.** After a finished map there is a 3% **calibration** chance that the bandit king takes a
runner hostage. Every device stops ("Opening maps is disallowed while Atlas Runners have been kidnapped at other map
devices"), and the player picks one of three answers:

| Answer | What it does |
|---|---|
| Rescue (`RescueCapturedMappers`) | a fight: the device's `powerEff` against the bandits' `3.0 + 0.5 × tier`. Win and the runner comes home; lose and **every runner on that device is lost** |
| Ransom (`RansomCapturedMappers`) | pay `50000` from the treasury and the runner comes home |
| Abandon (`AbandonCapturedMappers`) | the device's runners are given up; the device is emptied |

Unanswered after 24 hours, the bandits keep the runner and the device's runners are lost.

## 7. Client wiring, and what is still missing

* The panel is opened with S→C `0x7b` kind `0x5c` (`InstanceWaypoints.OpenUi(0x5c, …)`); the buttons come back as
  C→S `0x2ca` action bytes.
* **The numeric action ids are not known yet** except `0x12` (pause after this map). Until they are read off real
  clicks, the line is driven by server commands (`/mapping …`) and reports through the game log, the way the farm and
  the port do it (`/village`, `/port`).
* The state the panel needs (per map: completion %, time, death chance; per worker: rank, wage, level; the queue of
  twelve; the rewards waiting) goes out over the village's `0x2c9` state channel once its subtag layout is known.
  Until then the state is server-side only.
* The wire format of both channels is the framework's: `InstanceRulesets` (`src/GameServer.Core/Protocol/Instance/
  InstanceRulesets.cs`) frames `0x2c9` and `0x2ca` as `u16 id | u8 ruleset row | compact-uint length | payload`.
  The village module reads a `0x2ca` action with `InstanceRulesets.ReadAction` (the row is the record the panel
  belongs to — `0x9b` the town, `0x74` the farm — and the payload's first byte is the action).

## 8. Open reverse-engineering questions

1. The action byte for every mapping button (start, exit, rescue, ransom, abandon, acknowledge, hire, fire, reroll,
   open rewards).
2. The `0x2c9` subtag layout the map-runners panel reads, and the `0x1f` record kind that carries the mapping state.
   The town record itself is known (see §1.1) and holds none of it, so the mapping state is either a second record
   kind or a field on the `ClientVillage`.
3. Whether the estimate the client shows is computed by the client (from the worker's rank and the map) or sent by
   the server; if the client computes it, its exact curve would replace the calibration in §3.
4. What the client's `VillageJobTypes` "Trapped in the Atlas" (`LostMapping`) job is used for.
