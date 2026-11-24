# 30 — HUD signals, timers, PvP queue, party votes, and Endless Heist contracts

Static reverse-engineering notes for four HUD / dialogue wire protocols in the 3.29.3c/3.29.3d client.

**Evidence basis.** Code addresses (`.text` functions, thunks, jump tables) and message IDs were verified by static
disassembly of `Client/Game.exe` build 328228 (3.29.3d; identical `.pdata` functions, `.text` code VAs, and message
IDs to 3.29.3c release build 327984 per [re/tooling.md](../re/tooling.md) §11) and `data/client.db`. Message class
vtables (`Cls`) are the 3.29.3c release addresses from [schema.json](schema.json) and [c2s.json](c2s.json). No
3.29.3d-only data addresses are recorded here. ClientStrings lookup IDs are one-based unless labeled as zero-based database rows. No live server-client trace has been run for these four areas yet.

---

## 1. Achievements and Challenges (S→C `0x124`–`0x127`, C→S `0x123`)

Visible tabs in the Challenges panel come from the optional `vec8` of `AchievementSetsDisplay` IDs in S→C `0x1d`
([03](03-area-and-world.md) §1, stored at `instance +0xc418`). Opening or closing the panel sends C→S `0x123`
(3 bytes, `u8` 1/0; [15](15-progression.md) §2).

All four progress and completion signals are published exclusively by the instance S→C dispatcher `0x142150ae0`
(its `r13` is `instance + 8`, so `r13 + 0x7cf0` is `instance + 0x7cf8`):

| S→C | Case | Handler | 3.29.3c `Cls` | Write / Read | Instance signal | Wire layout (after `u16 BE` opcode) |
|---|---|---|---|---|---|---|
| `0x126` | `0x4e` | `0x142155774` | `0x1435e6968` | `0x1421acac0` / `0x1421acb70` | `+0x7cf8` | `u8 setId`, `u16 BE rawLen`, `rawLen` bytes copied to `msg +0x14`: `u16 LE bitsetByteCount` followed by `bitsetByteCount` bytes (`rawLen = bitsetByteCount + 2`; ctor `0x1421aca80` zeroes 256 bytes at `+0x16`, default `setId = 2`; reader does not clamp `rawLen`) |
| `0x125` | `0x4d` | `0x142155794` | `0x1435ca200` | `0x141f99160` / `0x141f991c0` | `+0x7d78` | `u16 BE rowIndex` — **0-based** `AchievementItems.dat` row index (stride `0x30`, bounds-checked in `0x14223f970`) |
| `0x124` | `0x4c` | `0x1421557f1` | `0x1435ca200` | `0x141f99160` / `0x141f991c0` | `+0x7df8` | `u16 BE rowIndex` — **0-based** `Achievements.dat` row index (stride `0x4a`, bounds-checked in `0x142361330`) |
| `0x127` | `0x4f` | `0x14215584e` | `0x1435ca200` | `0x141f99160` / `0x141f991c0` | `+0x7e78` | `u16 BE rowIndex` — **0-based** `AchievementItems.dat` row index (stride `0x30`, bounds-checked in `0x14223f970`) |

### Verified client consumers

- **`+0x7cf8` (S→C `0x126`, per-set progress bitset)**: notifier `0x1421773b0(signal, setId, &msg+0x14)` passes
  `setId` and a pointer to the `u16 LE` length-prefixed bitset buffer. Consumed by `ChallengesPanel` (`0x1407fa820`),
  the secondary challenges panel (`0x1408037a0`), and the Steam achievement sync (`0x140111f60` → `0x140112290`).
- **`+0x7df8` (S→C `0x124`, achievement / challenge completed)**: passes a handle to the resolved `Achievements.dat`
  row (`+0x08` `Description` string, `+0x10` `i32 SetId` where `2` = permanent Achievements and other sets are league
  Challenges, `+0x31` silent/skip-toast flag):
  - **PC HUD (`0x140dba5d0` → `0x140dd2d70`)**: skips if row is null, `[row +0x31] != 0`, or UI option `0x1c`
    (`0x141918e00`) is false; otherwise calls `vcall [[HUD +0x5b8] +0x398]` (`HUDNotificationDisplay::AchievementCompleteNotification`,
    `0x1402a4320`), which selects `ClientStrings` `0x323` (`"Achievement Complete!"`) when `[row +0x10] == 2` or
    `0x324` (`"Challenge Complete!"`) otherwise, and displays `[row +0x08]` on `AchievementCompleteBackground`.
  - **Console HUD (`0x140d6d300` → `0x140d835a0`)**: calls `0x1402a4320(HUD +0x900, &row)` directly when
    `[row +0x10] == 2` (or option `0x1c` is false); otherwise calls `0x1402afa00(HUD +0x900, Description, 0)`.
  - Also consumed by `ChallengesPanel` (`0x1407fac50`) and Steam sync (`0x140111f60` → `0x1401123b0`).
- **`+0x7d78` (S→C `0x125`, achievement item completed)**:
  - **PC HUD (`0x140dd29a0`)**: skips when parent `Achievements` row has `HideAchievementItems` (`+0x1f`) or `+0x31`
    set; if UI option `0x1d` is on, formats `ClientStrings` 0-based row `0x2dc` (732, `HUDAchievementItemCompleteMessage`:
    `"{0}: {1} (Complete)"`) into a toast (`0x14022e8d0` → `0x14022f4f0`). Separately, if UI option `0x24` is on and
    `AchievementItemRewards.dat` (`0x140d98a80`) has a matching entry, pushes a reward toast (`0x14024d5d0` →
    `0x14022f4f0` on `HUD +0xc18`).
  - **Console HUD**: listener `0x140d832d0`. `ChallengesPanel`: `0x1407fab20`.
- **`+0x7e78` (S→C `0x127`, achievement item failed)**:
  - **PC HUD (`0x140dd2dd0`)**: gated by parent `+0x1f`/`+0x31` and UI option `0x1e`; formats `ClientStrings` 0-based
    row `0x2dd` (733, `HUDAchievementItemFailedMessage`: `"{0}: {1} (Failed)"`) into `0x14022e8d0` → `0x14022f4f0`.
  - **Console HUD**: listener `0x140d83660`.

### Bounded negative findings

- A full `.text` displacement scan for `0x7cf0`/`0x7cf8`, `0x7d70`/`0x7d78`, `0x7df0`/`0x7df8`, and `0x7e70`/`0x7e78`
  confirms no other network dispatcher (`0x142145ae0` login, `0x14218ddf0` world, or other cases in `0x142150ae0`)
  publishes to these four lists.
- The Steam notifier (`0x140111f60`) also subscribes `0x140112630` to `instance +0x7ef8`; that signal is raised only
  from client state code (`0x140d52e60`, `0x140d5b010`), not from any S→C packet.

---

## 2. Mission Timers and Countdown Timers (S→C `0x163`, S→C `0x162`)

Both HUD timer widgets are driven by adjacent instance S→C opcodes (`0x162` and `0x163`). Neither is dead code:

| S→C | Case | Handler | 3.29.3c `Cls` | Write / Read | Instance signal | Wire layout (after `u16 BE` opcode) |
|---|---|---|---|---|---|---|
| `0x163` | `0x6a` | `0x142156ba8` | `0x1435ca870` | `0x141f99580` / `0x141fa67c0` | `+0x91f8` | `u8 timerType` (**1-based** `MissionTimerTypes.dat` row + 1, `1..18`), `u32 BE seconds` (`0` hides) — 7 bytes total |
| `0x162` | `0x69` | `0x142156bfa` | `0x1435cae30` | `0x141f9a710` / `0x141f9a790` | `+0x9278` | **`u32 BE count`**, **`u32 LE countdownTimerId`** (mixed endianness; **1-based** `CountdownTimers.dat` row + 1) — 10 bytes total |

### Mission Timer (`S→C 0x163` → `0x1402296e0`)

1. **Pre-`0x21` buffering (`0x142156bb8`)**: when instance state `[r13 + 0xc398] != 3` (`instance +0xc3a0 != 3`),
   `0x142156bd5` stores `(u32 timerType, u32 seconds * 1000)` into `instance +0xce80` (`r13 + 0xce78`) via
   `0x14215c840`. When `S→C 0x21` enters the area (`0x14215d6d0` at `0x14215dcf0..0x14215dd2c`), each queued entry is
   divided back by 1000 and emitted to `instance +0x91f8` via `0x140323b60`, then the queue is cleared.
2. **In-game dispatch (`state == 3`)**: `0x142156bc2` fires `instance +0x91f8` immediately with `(timerType, seconds)`.
3. **HUD routing**: `0x140dba5d0` (PC) registers `0x140dce320` and `0x140d6d300` (Console) registers `0x140d7fc60` on
   `instance +0x91f8`:
   - If `timerType == 8` (`Heist`, `MissionTimerTypes` row 7) and the Heist alert HUD (`HUD +0xd18` PC / `+0x968`
     Console) is non-null, tail-jumps to `0x1404c3ad0` (Heist lockdown countdown).
   - Otherwise, tail-jumps (`0x140dce34c` PC / `0x140d7fc8c` Console) to `MissionTimerManager::Start` (`0x1402296e0`)
     on **`HUD +0x4e0`** (PC) or **`HUD +0x880`** (Console) with `(manager, seconds, timerType)`. Note: `HUD +0x770`
     in `0x140dad910` (`0x140db0219`) is the parent container passed to factory `0x140da2cf0`, not the manager pointer
     itself.
   - `MissionTimerManager` ctor (`0x140229170` at `0x14022943c..0x140229497`) creates one child per `MissionTimerTypes`
     row with **1-based** ID `row + 1` (`1..18`) stored at child `+0x2c0` (`1 = Generic`, `2 = Incursion`, ...,
     `8 = Heist`, `9 = Ultimatum`, `10 = Royale` using a `0x308`-byte subclass, ..., `18 = HideoutMaze`). In
     `0x1402296e0`, `seconds == 0` hides the matching child (`0x140229772`); nonzero shows it and sets float seconds
     at `+0x2b8`.
4. **Secondary `+0x91f8` listener**: the Ultimatum "Return to the Ring" widget (`0x14069f510` → `0x14069f730`) also
   subscribes to `+0x91f8` and consumes `timerType == 9` (`Ultimatum`, row 8).

### Countdown Timers (`S→C 0x162` → `0x14071e470`)

1. **Wire endianness**: `0x141f9a710` (write) and `0x141f9a790` (read) byte-swap the first `u32` (`+0x10`, `count`)
   with `htonl`/`ntohl` (**big-endian**), but write and read the second `u32` (`+0x14`, `countdownTimerId`) raw
   (**little-endian**).
2. **Dispatch chain**: `0x142156bfa` fires `instance +0x9278` (`r13 + 0x9270`) via `0x140323b60(signal, count, id)`
   with no pre-`0x21` buffering. Both `0x140dba5d0` (PC) and `0x140d6d300` (Console) register the virtual-dispatch
   thunk `0x140dabd9c` (`mov rax, [rcx]; jmp [rax + 0xbf8]`) on `+0x9278`:
   - PC HUD vtable `+0xbf8` → `0x140dd9120`: loads `HUD +0x508` (`"countdown_manager"`, built by `0x140da1b40`) and
     tail-jumps to `0x14071e470` when non-null.
   - Console HUD vtable `+0xbf8` → `0x140d8e080`: loads `HUD +0x878` and tail-jumps to `0x14071e470` when non-null.
3. **Parameters (`0x14071e470`)**: `edx = count`, `r8d = countdownTimerId`. If `count == 0` or `countdownTimerId == 2`,
   the widget hides (`0x14071e681`). Otherwise it looks up `Data/CountdownTimers.dat` row `countdownTimerId - 1`
   (stride `0x50`; `1` = row 0 `MercenaryCountdown`), initializes the float countdown to `(float)count`, and shows the
   widget.

### Bounded negative findings (other timer paths checked)

- **`SetMissionTimer`** (`0x140074a10` in `0x14006a5f0`, `0x141edb11b` in `0x141ece040`): registered alongside
  `NPCUnaggro`, `AggroInRange`, and `UnaggroInRange` as a server-only GEAL script built-in (mode `2`/`3` via
  `0x141ee8450`), with no client HUD execution path.
- **`Timer` component (`typeId 211`)**: factory `0x141e45830` installs a primary vtable whose slots 17 (creation blob)
  and 20 (`S→C 0x2bc` update) are both `ret` (`0x1400f1180`). It carries no wire state and does not drive HUD timers.
- **`S→C 0x2c9`** (class `0x1435ca360`): routes to the default no-op case (`0xfd`) in `0x142150ae0`.
- **`S→C 0x11f`** (case `0x4a`, `0x142155723`, `+0x90f8`, buffered at `instance +0xcfa8` before `0x21`): drives the
  PvP match timer and scoreboard (`0x140dcd180`, `HUD +0x4b8`/`+0x4c0`), not `MissionTimerManager`.

---

## 3. PvP Queue Bar and Related PvP Messages

### Core queue bar messages

| Dir | Opcode | Case / Len | 3.29.3c `Cls` | Write / Read | Handler / Sender | Wire layout (after `u16 BE` opcode) |
|---|---|---|---|---|---|---|
| S→C | `0x118` | case `0x45` | `0x1435e7120` | `0x1421ac250` / `0x1421ac320` | `0x14215551a` → `+0x8cf8` | `u8 otherPlayers` (`+0x12`), `wstr queueName` (`+0x18`/`+0x28`), `u8 isChallenge` (`+0x10`, bool), `u8 showWithdraw` (`+0x11`, bool), `u64 LE nextMatchTime` (`+0x48`, raw 8 bytes) |
| S→C | `0x119` | case `0x46` | `0x1431768d8` | `0x140911160` / `0x1400f1180` | `0x142155612` → `+0x8d78` | empty (2 bytes total) — cancels/hides the PvP queue bar |
| C→S | `0x11a` | 2 bytes | `0x1431768d8` | `0x140911160` | `0x140ac1750`, `0x1408e7070` | empty — "Leave Queue" button (ClientStrings ID `0x447`) on PvP queue bar, and decline on match-ready dialog |
| C→S | `0x18d` | 2 bytes | `0x1431768d8` | `0x140911160` | `0x140ac1700` | empty — "Withdraw" button (ClientStrings ID `0x448`) on PvP queue bar |

### Client behavior (`S→C 0x118`, `0x119`)

- **PC HUD `+0x8cf8` listener (`0x140dccbc0`; Console `0x140d7e400`)**: subtracts the client's server-clock offset
  global from `nextMatchTime`. When the adjusted timestamp is `> 0` and ahead of `time(0)` (`0x142cfaf10`), it shows
  the PvP timer (`HUD +0x4b8`) and starts it with `(float)(adjusted - now)` via `0x140abde90`. It then tail-jumps to
  `PvPQueueBar::Set` (`0x140abcc50`) on `HUD +0x4b0` (`0x140abc0f0`):
  - `nextMatchTime != 0`: title `ClientStrings 0x446` (`"PvP"`), text `0x44d` (`"Waiting for next match..."`).
  - `nextMatchTime == 0 && isChallenge != 0`: title `0x445` (`"PvP Challenge"`), text `ClientStrings` 0-based row
    `0x448` (`WaitingForAccept`: `"Waiting for {0} to accept..."`) formatted with `queueName`.
  - `nextMatchTime == 0 && isChallenge == 0`: title `0x446` (`"PvP"`), text 0-based row `0x44b`
    (`PVPQueueFindingMatchFor`: `"Finding match for {0}..."`) formatted with `queueName`.
  - `otherPlayers > 0`: appends `ClientStrings` `0x44a` / `0x44b` (`"{0} other player"` / `"{0} other players in their party."`).
  - `showWithdraw`: toggles visibility of the "Withdraw" button (`bar +0x2c8`, `0x140abd43b`), whose click handler
    `0x140ac1700` sends C→S `0x18d`. The "Leave Queue" button (`bar +0x2c0`) calls `0x140ac1750` and sends C→S `0x11a`.
- **PC HUD `+0x8d78` listener (`0x140dccce0`, S→C `0x119`)**: hides `HUD +0x4b0`, and hides `HUD +0x4b8` when the
  current area is not a PvP arena (`0x1400f25c0`).
- **Adjacent PvP signals in `0x142150ae0`** (for completeness; full UI consumers remain unread):
  - `S→C 0x117` (case `0x44`, empty, `0x142155509` → `+0x8bf8`);
  - `S→C 0x11b` / `0x11c` (cases `0x47` / `0x48`, `0x1435e7080`, `0x1421556aa` / `0x1421556c7` → `+0x8e78` / `+0x8ef8`);
  - `S→C 0x11e` (case `0x49`, `0x1435e68c8`, `u8` + `f32` + `u16` + sub `0x1421c1370`, `0x1421556e4` → `+0x9078` → PC `0x140dccda0` states `0..8`);
  - `S→C 0x11f` (case `0x4a`, `0x1435e74c0`, `u8` + `u32`, `0x142155723` → `+0x90f8` → PC `0x140dcd180` states `0..6`);
  - `S→C 0x189` (case `0x76`, `0x1435ca068`, `wstr` ignored by handler `0x142156fd7` → `+0x8df8` → PC `0x140dccd40` shows `HUD +0x4c8`);
  - `C→S 0x116` (26 bytes, `0x1435ca4e0`, senders `0x1403f8670`/`0x14040ac30`) and `C→S 0x11d` (4 bytes, `u16 BE`, `0x1435ca200`, sender `0x1408e6ff0`).

---

## 4. Party Votes (S→C `0xbe`, C→S `0xbd`)

`HUD +0x4e8` (`VoteDisplay`, ctor `0x14026e170`, built by `0x140d9b130`) handles both solo and party Bandit votes
(Act 2 Alira, Oak, Kraityn; see [17](17-npcs.md) §1 for the solo summary). `VoteDisplay::SetInstance` (`0x14026e4d0`,
called from PC `0x140dba5d0` and Console `0x140d6d300`) subscribes `0x14026fdb0` → `0x14026e690` to `instance +0x82f8`.

### S→C `0xbe` (dispatcher case `0x33`, `0x142153f84`)

3.29.3c `Cls` `0x1435e6d40`, write `0x1421a9f80`, read `0x1421aa0d0` → `0x141fe35f0`. Fires `instance +0x82f8`
(`r13 + 0x82f0`) with `&msg + 0x10`:

```
u16 BE  0x00be
u8      voteTypeId          +0x10  1-based VoteType.dat row + 1 (0 = close/hide; 1 = BanditVoteInt, 2 = BanditVoteStr, 3 = BanditVoteDex)
u8      answered            +0x30  bool (non-zero dims/disables buttons)
u8      localVoter          +0x31  bool (controls visibility with answered state)
u8      voterCount                 0..255 (writer throws above 255)
repeat voterCount times (0x28-byte entry in memory):
  wstr  characterName       +0x00  compact wide string with u16 BE code-point count (0x140e0b250 / 0x140e0b480)
  u8    voteStateId         +0x20  1-based VoteState.dat row + 1:
                                     1 = KillBandit   ("Kill the bandit")
                                     2 = HelpBandit   ("Help the bandit")
                                     3 = WaitingForVote ("Waiting to vote...")
                                     4 = WaitingToEnterArea ("Waiting for player to enter area...")
```

Both table IDs are **1-based** (`0x14026e732: lea ecx, [rax - 1]` for `VoteType.dat` stride `0x30`; `0x14026ec73: dec ecx`
for `VoteState.dat` stride `0x10`). Sending `voteTypeId = 0` clears and hides `VoteDisplay`.

### C→S `0xbd` (3 bytes)

3.29.3c `Cls` `0x1435ca088`, write `0x140e27920`, sole sender `0x14026ff10` (bound to the Help/Kill buttons inside
`0x14026e690`):

```
u16 BE  0x00bd
u8      choice              1 = Help button (+0x2d8), 0 = Kill button (+0x2e0)
```

### Bounded negative findings

- `Data/VoteType.dat` (loaded only in `0x14027d750`) and `Data/VoteState.dat` (loaded only in `0x14026e170`) have no
  other callers in `Game.exe`.
- `instance +0x82f8` has no other network publisher in `0x142150ae0` (S→C `0xbe` is the sole writer). Ultimatum
  party modifier voting uses a separate window and protocol.

---

## 5. `EndlessHeistContracts` (`NPCTalkCategory` row 39 / `0x27`)

### Verified data (`data/client.db`)

- **`NPCTalkCategory`** row `39` (`0x27`, 0-based; 1-based `0x28` = 40): `Id = "EndlessHeistContracts"`, `__col1 = 1`.
- **`NPCTalk`**: exactly one row references Category `39` — row `2424` on NPC `457`
  (`Metadata/NPC/League/Heist/Driver`, Adiyah, the Wayfinder), option index `__col1 = 17`,
  `DialogueOption = "Buy Contracts"`, `Script = ""`, `Script2 = ""`, `Inventory = None`, **`__col27 = 43` (`0x2b`)**,
  `__col29 = 60` (`0x3c`, neutral).
- **`NPCShop`**: row `37` is `Id = "EndlessHeistContracts"`, window kind `0` (ordinary `VendorBuy` shop), empty
  static item list (`[]`), analogous to `NPCShop` row `32` (`EndlessDelve`).
- Related event-league rows still shipped in `data/client.db`: `WorldAreas` row `1708` (`HeistHubEndless`),
  `QuestFlags` `2603`–`2610` (`EndlessHeistPassiveRewardsStart`, `EndlessHeistPassiveRewards1`–`7`), and
  `ClientStrings` `6014`–`6015` (`EndlessHeistAscendancyEnabled`, `EndlessHeistMoreAscendancyPoints`).

### Verified client code behavior & bounded negative proof

1. **League-category gating (`0x141e7bb90` at `0x141e7bbde..0x141e7bc06`)**: `NPCTalk +0x124` (`__col27`) must equal
   `[world + 0xd0]` whenever it is not `0x3c` (60). In the client's league-category string table (set from S→C `0x1d`
   `+0x28` via `0x142149550`), `0x2b` (43) is `"EndlessHeist"`. Thus Adiyah's `"Buy Contracts"` option appears only
   when `world +0xd0 == 0x2b` (the same check in `0x141eb32c0` / `0x1404c0f40` / `0x1404c05e0` also zeroes Rogue's
   Marker contract fees during Endless Heist).
2. **Availability switch (`0x141e7bb90`, byte table `0x141e7cf18`, jump table `0x141e7cec8`)**: covers all 64
   `NPCTalkCategory` rows (`0x00..0x3f`). Row `39` (`0x27`) maps to the default case (`0x141e7ceb5`) — no extra
   client-side predicate beyond `__col27 == 0x2b`.
3. **Click handler (`0x140723fa0` PC, `0x1407354f0` Console)**: converts `NPCTalk +0x64` to a **1-based** category ID
   (`row + 1`) by dividing by the 9-byte row stride (`imul 0x1c71c71c71c71c72`). Only 1-based `0x06` (`Topic`, row 5),
   `0x1a` (`RespecHellscapePassives`, row 25), and `0x27` (`ViewPantheon`, row 38) are handled locally. Row `39`
   (1-based `0x28`) falls through to `0x1407240d6` and sends ordinary **C→S `0x82`** (`u8 = 17`).
4. **Exhaustive category scan**: scanning all of `.text` for the 9-byte `NPCTalkCategory` division magic
   `0x1c71c71c71c71c72` finds 31 functions; none of the NPC/UI consumers (`0x140720d10`, `0x140723fa0`, `0x140726830`,
   `0x140727f30`, `0x140733630`, `0x1407354f0`, `0x140735d30`, `0x14073a990`, `0x141e7bb90`, `0x141e7d730`) branches on
   row `39` (`0x27` / 1-based `0x28`).
5. **S→C `0x7b` (`0x142169dd0`, jump table `0x14216bb74`, kinds `0x00..0x72`)**: contains no Endless Heist UI kind;
   Heist has only `0x34` (`HeistMission`), `0x35` (`HeistBlueprint`), `0x36` (`HeistNPCInventory`), and `0x37`
   (`HeistStorageWindow`).
6. **Conclusion**: `EndlessHeistContracts` has no dedicated client UI, art, or opcode in 3.29.3c/d. The client data supports the following inferred server response. When a server runs
   an Endless Heist instance (`world +0xd0 == 0x2b`) and receives `C→S 0x82 0x11` for Adiyah, it can answer via the
   standard vendor shop protocol (`S→C 0xc0` manager 1 shop tabs + `S→C 0x8c` window kind `0`, matching `NPCShop` row
   `37`). The server-side contract stock list itself is not present in client data (`NPCShop` row `37` has an empty
   item list) and remains server-defined.
