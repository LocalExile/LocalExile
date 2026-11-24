# Kingsmarch village — the fact layer

This file holds **observations only**: what was read in the client binary, in the server code, or seen in a
running client. It carries no design advice and no "the server should" sentences; those belong in
[the judgement layer](../game/settlers.md), whose every claim cites an entry id from here (`F-xxx`).

One entry = one observation = the address or file:line, what was read or observed, and where that came from.

## How to read an entry

    - **F-000 · short name** — `level` — observation. Evidence: `file` §section (or "live YYYY-MM-DD").

* `level` is one of **confirmed** (seen in the client, or verified live — the entry says when and how),
  **candidate** (read off the binary or off the source, not yet seen at runtime) and **unknown** (no evidence;
  the entry always gives the reason it is unknown, and says "unreachable along the paths we read" rather than
  "nobody calls it").
* Evidence files are under `Tools\re\out\kingsmarch\` and are abbreviated as:
  `RE-HANDOFF` = `RE-HANDOFF.md`; `B-content` = `B-content\REPORT.md`; `A-frame` = `A-frame\REPORT.md`;
  `C-trigger` = `C-trigger\REPORT.md`; `round3` = `cloud-TASKS-round3.md`;
  `round4/X` = `cloud-round4\X-*.md` (the round-4 Ghidra answers, one letter per question).
* Server paths are relative to the repository root of this worktree (`F:\local exile\.worktrees\main-kingsmarch`).

## Address convention

`F-002` is the rule every other entry follows: **code addresses and message ids** are the release build's and
hold for every client build the server accepts; **data addresses** (globals, vtables, string literals, tables)
do not. So an entry that names a vtable or a global is evidence about 3.29.3c/build 327984 only, and the
judgement layer does not build a protocol on one.

---

## 1. Client identity and conventions (F-001–F-005)

- **F-001 · client build** — `candidate` — Path of Exile 3.29.3c, build 327984; binary
  `F:\local exile\Client\Game.patched.exe`, ImageBase `0x140000000`, SizeOfImage `0x4D9A000`, `.text` RVA
  `0x1000` size `0x2F4BB6E`. Evidence: `RE-HANDOFF` §1.1 (repeated in the header of `round4/D`, `round4/E`,
  `round4/F`, `round4/H`).
- **F-002 · address convention** — `confirmed` — code addresses and message ids hold for every allowed build;
  data addresses (globals, vtables, strings) do not. Evidence: `AGENTS.md` ("Addresses in the docs and scripts
  are the 3.29.3c release's (build 327984)…"), `RE-HANDOFF` §1.2.
- **F-003 · village pointer chain** — `candidate` — global `0x1445D6FD8` is the root; `[root+0x348]` is the
  `ClientVillage` (vtable `0x14346CC18`, a data address). Evidence: `RE-HANDOFF` §2.1.
- **F-004 · HUD member windows** — `candidate` — `[village+0x410]` is the HUD; the Kingsmarch panels are HUD
  members at `+0x9a0` settlers, `+0x9b0` recruitment, `+0x9c0` port, `+0x9d8` farm, `+0xa00` town map (vtable
  `0x1430DECF0`) and `+0xa08` panel host (vtable `0x1430D29A0`). Evidence: `RE-HANDOFF` §2.1.
- **F-005 · record list measured empty** — `confirmed` — `village+0xCFD8` and `+0xCFE0` were read in a live
  town and were equal (no settlement record present at that moment). Evidence: `A-frame` §2, live 2026-11-22.

## 2. The town-map window and its widget (F-006–F-013)

- **F-006 · window object** — `candidate` — the town map is `[hud+0xa00]`: 0x540 bytes, ctor `0x1405235c0`,
  `Show` `0x140529450`. Its widget is `[win+0x300]`: 0x308 bytes, ctor `0x140521d00`, vtable `0x1430DE078`.
  Evidence: `RE-HANDOFF` §2.3, `B-content` §0/§2.
- **F-007 · Show's three gates** — `candidate` — `0x140529471 cmp [rcx+0x2e0],rbp / je 0x140529632`;
  `0x1405294F0` tests `[rsi+0x2f0]`; `0x140529504` tests `[rsi+0x2b8]`. Any of the three null makes `Show`
  return without building the window. Evidence: `B-content` §0, `RE-HANDOFF` §2.3.
- **F-008 · Show's chrome tail** — `candidate` — `0x140529582` sets `+0x520` = `0x0101…` (two bytes),
  `+0x523`, `+0x524 = 1.0f`, `+0x528 = 1`, and plays the sound `"SettlersLeague_TownMapOpen"`.
  Evidence: `B-content` §0.
- **F-009 · container behind `+0x2e0`** — `candidate` — the container is 760 bytes (0x2F8), ctor `0x1418cc180`,
  vtable `0x1434AA7E0`, built by factory `0x140363210` from `[win+0x2D0]+0xB8`. For the town map
  `[win+0x2D0]` is the `ClientVillage`, `village+0xB8` measured `0x38000001D0` (an integer, not a pointer), so
  the factory faults reading address 0. Evidence: `A-frame` §3/§4, `B-content` §1.
- **F-010 · container row predicate** — `candidate` — the predicate at `container+0x2A0` slot 0 is called
  through `0x1424c6440`; a return below `[list+0x2c]` hides the row (localised text id `0x1d9b`), and
  `0x1400ECFF0` (`33 c0 c3`) is a stand-in predicate that always says "draw it". Evidence: `A-frame` §4,
  `B-content` §1.
- **F-011 · widget finds the record once** — `candidate` — `[win+0x300]+0x2f0` is the kind-0x9B record, found
  at `0x140522883` / `0x1405228F7` by scanning `village+0xCFD8`; the scan runs inside the widget ctor, so the
  record must be there before the area starts. Evidence: `B-content` §2, `C-300.md`.
- **F-012 · widget's job row** — `candidate` — `[win+0x300]+0x2b8` is a `VillageJobs` row, written by
  `0x140523520`, which copies the row's text at `[[row]+0x10]` (the `Status` column). Row stride 0xB8, element
  id `+0x408`, row reference `+0x458`, observer callback `0x14052b100`. Evidence: `B-content` §2.
- **F-013 · VillageJobs table** — `candidate` — 31 rows; columns `JobType:int, __col1:int, Status:string,
  Stat:int|null, StatValue:int`; row 0 "Idling", row 1 "Mining Crimson Iron" (`Stat` 20714), row 2 "Smelting
  Crimson Iron" (20716). Evidence: `B-content` §2.2.

## 3. Records, and the ids that reach them (F-014–F-022)

- **F-014 · town record 0x9b** — `candidate` — 0x5A8 bytes, ctor `0x141767BA0`, vtable `0x14348E820`,
  message handler `0x141767F60` (vtable slot 8). Evidence: `round4/I` §0, `round4/F` §1.
- **F-015 · record 0x74** — `candidate` — 0x2E0 bytes, ctor `0x141687370`, vtable `0x14347BB70`, handler
  `0x14168A080`. Its ctor needs the `m_JobLevelCaps` table (id `0x86A`) inside
  `[*(village+0xC278)+0xE0…+0xE8]`, else it asserts with "Attempted to get invalid index for
  'm_JobLevelCaps'" (string `0x14347BBB8`); its draw routine `0x1416883A0` indexes the wire marker vector
  `+0x1E8` with no null check, and `+0x2D4` must equal `([+0x1F0]-[+0x1E8])/8`. Evidence: `round4/I` §4.3.
- **F-016 · 0x1f creates a record** — `candidate` — the factory table is `0x14475DC70`, indexed by the kind
  (≤ 0xAE); the town kind's path is `0x1415a6800` → allocator `0x141763b20` → record ctor, and a kind already
  present is skipped. Evidence: `round4/I` §0, `Modules/Core/Game/World/KingsmarchTown.cs:67-75`.
- **F-017 · ClientVillage object** — `candidate` — ctor `0x1415A65D0`, clone `0x1415A7310`; the record-adding
  method is vtable slot 8 `0x1415a6800`, and it has no direct callers (it is reached only through the vtable).
  Evidence: `B-content` §3.2.
- **F-018 · 0x2c9 frame** — `candidate` — `u16 id` (on the wire the bytes are `02 c9`) `| u8 kind |`
  compact-uint length `| payload`, with `payload[0]` the subtag. The server's builder is
  `Modules/Core/Village/VillageModule.cs:289-300` (length is 1, 2 or 3 bytes, switching at 0x80 and 0x4000).
  Evidence: `round4/I` §0, `VillageModule.cs:289-300`.
- **F-019 · 0x2c9 reader** — `candidate` — `0x141fa7ca0` keeps the record kind at `+0x90` and hands the payload
  to the `ClientVillage` vtable slot 8 (`0x141767F60`), which reads one subtag byte and compares it against
  0…13 in a chain. There is no resync: a frame the reader cannot parse takes the connection with it.
  Evidence: `round4/I` §0, `VillageModule.cs:286-288`.
- **F-020 · 0x7b frame** — `candidate` — `0x7b | u8 kind | four u32`; the client screen table `0x142fd89f0`
  names `0x54` Recombinator, `0x55` Recruitment, `0x56` TownMap, `0x57` Mine, `0x58` Port, `0x59` Farm, `0x5a`
  BlackSmith, `0x5b` Disenchanting, `0x5c` MapRunners, `0x5d` Fishing, `0x5e` CurrencyExchange, and `0x35`
  carries a `VillageJobs` row. Evidence: `KingsmarchTown.cs:85-89`, `VillageModule.cs:14-17`.
- **F-021 · 0x7b 0x56 drew the world map** — `confirmed` — the server sent `007B56…` for NPCTalk row 3610
  ("Manage Town") and the client drew the **WORLD** map window, not the Kingsmarch town map. Live
  2026-11-20 20:38:20; screenshot `logs\shots\C-talk\frame-0001-000544ms.png`; the client read it as WORLD /
  Epilogue in the same session's image check. Evidence: `C-trigger` §4.4/§4.5.
- **F-022 · the dispatcher table is not the one used** — `candidate` — the instance table at `0x142150AE0`
  maps `0x1F`, `0x1E`, `0x2C9` and `0x7B` to `0xFD` (unhandled) and `0x2CA` to `0x14215AD10`; a whole-`.text`
  scan finds no rip-relative reference into `0x14215B300…0x14215BB00`. So those ids do not reach their
  handlers through that table. Evidence: `round4/I` §5.

## 4. Fields of the kind-0x9B record (F-023–F-037)

- **F-023 · worker vector `+0x160`** — `candidate` — stride 0xB8, row ctor `0x141776900`; subtag 0 replaces or
  resizes the vector (grow `0x141778870`, shrink `0x1405574B0`), subtag 5 appends one row. Reader chain
  `0x1424C7F80` → `0x1424C7CA0` → `0x1424C9080` / `0x1417737D0`. Evidence: `round4/I` §1, `round4/F` §3.
- **F-024 · worker row fields** — `candidate` — `+0xA0` u16 wage, `+0xA8` the job reference (a u8 naming a
  `VillageJobs` row through `0x14223F970`), `+0x08` and `+0x18` 0x10-byte stat references, `+0x28` a vector of
  {0x10-byte reference; u8}. Evidence: `round4/I` §1.2, `round4/F` §6.
- **F-025 · recruit vector `+0x178`** — `candidate` — filled by subtag 1 through `0x141773300` and notified by
  `0x1417734C0`. Element stride: `0xB0` in `round4/F` §3; `round4/I` §1 says 0x10-byte elements for the same
  field — see F-095.
- **F-026 · treasury gold `+0x198`** — `candidate` — u32; the panel reads it at `0x140529A84` into element
  `win+0x318`; the only writer is the subtag-4 reader (`0x1424C824B`), plus the record ctor zeroing the block.
  Evidence: `round4/F` §4/§6, `round4/H` verdict, `B-content` §3.
- **F-027 · `+0x19C`** — `candidate` — u32 second gold field; no panel element reads it; the wage model uses it
  as the ETA base ("gold already spent on wages"). Evidence: `round4/F` §4/§6, `round4/H` (snapshot `+0x0C`).
- **F-028 · 21 resource counters `+0x1A0…+0x1F3`** — `candidate` — 21 u32, one per `VillageResources` row
  (index = Id): 0-4 PendingIronOre/PendingCopperOre/PendingAmber/PendingBismuthOre/PendingVerisiumOre,
  5-9 the five ores, 10-14 Wheat/Corn/Pumpkin/Globulora/Orbisfera, 15-19 the five refined bars, 20 MagicDust;
  drawn into `win+0x3C8[0…20]`. Evidence: `round4/H` §1.
- **F-029 · 15 production counters `+0x1F4…+0x22F`** — `candidate` — 15 u32, one per `VillageProduction` row.
  Evidence: `round4/F` §4, `round4/H` verdict.
- **F-030 · six farm flags `+0x230…+0x235`** — `candidate` — 6 bytes, one per job type, used to pick a
  resource row per card; the wage builder interns `Data/VillageFarmAdjacency.dat` for them (`0x1424C301D`) and
  `0x15` means "skip". Evidence: `round4/F` §4/§5, `round4/H`.
- **F-031 · pairs, blocks and packed bytes** — `candidate` — `+0x236/+0x23E/+0x246` = 3 × (u32 lo, u32 hi);
  `+0x24E/+0x266/+0x27E` = 3 × (u64 lo, u32 hi, u8 N, u8, u8, then N bytes); `+0x296` u64; `+0x29E` two packed
  nibbles; `+0x29F` packed bits. Evidence: `round4/F` §4, `round4/I` §1.
- **F-032 · `+0x2A0` stat context** — `candidate` — an embedded `GetStat` context (vtable `0x143035588`) that
  the snapshot passes as its fourth argument. Evidence: `round4/I` §1, `round4/F` §4.
- **F-033 · strings, hash map, triples** — `candidate` — `+0x470 + k*0x20` are 3 strings (subtag 0xD; an index
  outside 0…2 overruns the record); `+0x4E0` is a hash map with 3-byte keys (subtags 0xB/0xC); `+0x3E0 + i*0x18`
  are 6 tagged triples written by subtag 3 with no reader found. Evidence: `round4/I` §1, `round4/F` §3.
- **F-034 · selection bytes** — `candidate` — subtag 8 writes `+0x5A0 = v-1` and `+0x5A1 = 1`; `+0x5A2` is set
  by the record ctor from `[*(village+0xC278)+0xB0]` and `word[InstanceDetails+0x2A] == 0x8F53`, so no message
  can set it. Evidence: `round4/I` §1/§4, `round4/F` §3.
- **F-035 · InstanceDetails** — `candidate` — `[village+0xC278]`, 0x1510 bytes, vtable `0x14347A180`, ctor
  `0x1416563B0`; created only by `0x140D57090` (its single caller `0x140d0df40`, which logs id `0x88A904AA`,
  the login-server instance hand-off). Its sorted-vector maps are filled from the client's own data: the five
  lookup keys `0x2A6D6634`, `0xA354B023`, `0xA162470C`, `0xBFE765CB`, `0xADC2E849` occur only at their lookup
  sites, and no FNV-1a32 of a printable string matches them. Evidence: `round4/E` §2.
- **F-036 · fields no reader was found for** — `unknown` — `+0x150`, the `+0x3E0` triples (written by subtag 3),
  `+0x4D0`, `+0x2A8`. Reason: no read site was found in the disassembly of the panel and draw paths that were
  read; that makes them unreachable along the paths we read, not absent. Evidence: `round4/I` §3/§7.
- **F-037 · two vtables off by a few bytes** — `unknown` — the base vtables `+0x18` (`0x14348EAF8`) and `+0x158`
  (`0x14348E1F8`) sit a few bytes off the `lea` arithmetic in the disassembly. Reason: the exact object base for
  those two was not resolved. Evidence: `round4/I` §7.

## 5. 0x2c9 subtags (F-038–F-050)

- **F-038 · subtag 0 (whole worker list)** — `candidate` — payload `u8 N` then N worker rows; it replaces the
  vector at `+0x160` and notifies through `0x140B37970`. Server: `VillageModule.cs:186-199`.
- **F-039 · subtag 1 (recruit list)** — `candidate` — payload `u8 idx` then that many recruit elements; the
  count byte is read twice, by the handler (`0x141767fc1`) and by the reader (`0x141773338`), which is why the
  server sends it twice (`VillageModule.cs:212-213`); a single count makes the reader run past the blob.
- **F-040 · recruit element** — `candidate` — the worker row of F-042 plus a `u32` hire price;
  `VillageWorkers.Recruit` (`Modules/Core/Village/VillageWorkers.cs:68`) appends it. Evidence:
  `VillageModule.cs:201-216`.
- **F-041 · subtag 2 (dock slot)** — `candidate` — `u8 index` + two interned names + 16 × u32 into
  `+0x2C0 + idx*0x60` (names at `+0x40`/`+0x50`), notify `0x141773650`. Evidence: `round4/F` §3.
- **F-042 · worker row layout** — `candidate` — `u32 name key 1 | u32 name key 2 | u16 wage | u8 N |
  N × (u8 job type, u8 skill) | u8 VillageJobs row` = 13 bytes with N = 0 (12 + 2N); the reader is
  `0x1417737D0`. Server: `VillageWorkers.cs:10-11` (`Row` at `:53`). Evidence: `VillageWorkers.cs:10-11`,
  `round4/F` §3.
- **F-043 · subtag 3 (triples)** — `candidate` — `u8 idx` + 3 × u64 (each rotl56-encoded) into
  `+0x3E0 + idx*0x18`. Evidence: `round4/F` §3.
- **F-044 · subtag 4 payload** — `candidate` — 245 bytes when the three blocks are empty, in this wire order:
  21 × u32 resources; 15 × u32 production; u32 gold; u32 (`+0x19C`); 6 bytes (farm flags); 3 × (u32, u32)
  pairs; 3 × (u64, u8, u8, u8) blocks with N = 0; u64; 2 bytes; u64 clock; 4 packed bytes; then the trailing
  u64 timestamp. Sizes: block `237 + ΣN`, frame `245 + ΣN`, with the subtag byte `246 + ΣN`. Server:
  `VillageModule.cs:238-264` builds exactly this list. Evidence: `round4/F` §4 (line-by-line loads),
  `round4/H` verdict (245-byte block).
- **F-045 · subtag 4 clock rule** — `candidate` — the handler reads the trailing u64 at `0x1417683cc`, calls
  `0x142CFAF10` (FILETIME→Unix seconds) at `0x1417683D8`, `sub` at `0x1417683E1`, then
  `add qword [r14+0x190]` at `0x1417683E4`. The snapshot then treats `record+0x190 > now` as an error
  (`0x1424C45B0…0x1424C45BF ja`), logging id `0xFFE0ED8D` "Attempting to advance village state to a time in the
  past. was = {}, now = {}" (UTF-16 at `0x143645E80`, length 0x4d) and faulting at `0x140DEFC9A`. So the value
  must be Unix whole seconds; 0, milliseconds, FILETIME or an uptime all push the clock ahead of now.
  Evidence: `round4/E` §0/§1, `round4/F` §4/§5.
- **F-046 · subtags 6, 7, 8** — `candidate` — 6 takes `u8 index` and erases that worker row (`0x1405CCDD0`,
  `+0x168 -= 0xB8`); 7 takes `u8 rowIndex | u8 nameKey` (3 bytes) and rewrites `workerRow+0xA8`; 8 takes
  `u8 v` and writes `+0x5A0/+0x5A1` (v = 0 clears only `+0x5A1`). Evidence: `round4/F` §3.
- **F-047 · subtags 9 and 0xA (job overlays)** — `candidate` — `u8 index | u8 count | count × u32` overlays the
  record's table context at `+0x2A0` with `VillageJobs` rows (9) or `VillageJobTypes` rows (0xA, `mov edx,5`),
  notify `0x141773B00`. Evidence: `round4/F` §3.
- **F-048 · subtag 9 killed the client** — `confirmed` — three hard client exits on 2026-11-22 from subtag 9
  frames, fault `0x1402D1029`, code `c0000005`; a WER event 1001 at 2026-11-22 11:49:49 records the same fault.
  Evidence: cited in `VillageModule.cs:317-319` (comment) and the live notes.
- **F-049 · why subtag 9 faults** — `candidate` — the embedded widget `0x1402d0a90` (vtable `0x143062e68`)
  looks the id up in a keyed table at `supplier+0x168` (`0x141d65830`, 24-byte rows, binary search) and
  dereferences the result even when the id is missing; the keying table is encrypted on disk. The port panel's
  pin board `[ClientVillage+0x5C8]` exists only after the port window has been opened, so a quota pushed before
  that walks a NULL. Evidence: the previous version of `Agents/docs/game/settlers.md`, `VillageModule.cs:317-319`.
- **F-050 · subtags 0xB, 0xC, 0xD** — `candidate` — 0xB and 0xC are duplicate cases that insert a 3-byte key
  into the hash map at `+0x4E0` (`+0x8` buckets, `+0x10` mask, `+0x18` used, `+0x20` count, 0xC-byte slots at
  `bucket + (hash & 3)*3`); 0xD takes `u8 idx` + a length-prefixed string (`0x140E276C0`) and copies a
  0x20-byte value to `+0x470 + idx*0x20`. Evidence: `round4/F` §3.

## 6. C→S 0x2ca (F-051–F-057)

- **F-051 · frame and writer** — `candidate` — `u16 id | u8 kind` (the low byte of dword `[ctx+8]`)
  `| compact-uint length | blob`; `blob[0]` is the action byte, written by the argument object's serializer and
  not by the caller. The message ctor `0x141FA7A10` hardcodes `mov word [rcx+8],0x2ca`; the send helper
  `0x1421A1960` has 146 callers, so every call of it sends a 0x2ca. Evidence: `round4/D` §1.
- **F-052 · action layouts** — `candidate` — `0x00` u8 tier; `0x01`/`0x02` u8 row; `0x03`/`0x04` u8 id + u8
  row (`0xFF` = none), 3 bytes; `0x05` u8 index; `0x06` u8 slot + u8 row + 16 × u32, 67 bytes; `0x07` u8;
  `0x0A` none; `0x0B` u8; `0x0C`/`0x0D` u8; `0x0E` u8 + u8; `0x0F`/`0x10`/`0x11`/`0x17` none; `0x12` u8 on/off +
  u8; `0x13` 3 × u32 (13 bytes); `0x14` 6 × u32 (25); `0x15` 5 × u32 (21); `0x16` u32 (5); `0x18`/`0x19` u32
  amount (5); `0x1B`/`0x1C` u8; `0x1D` u8. Every field little-endian, no length prefix, no tag; `0x08` and
  `0x09` were not found in this family. Evidence: `round4/D` §0/§2.
- **F-053 · the serializer writes the action byte** — `candidate` — every serializer in the family starts
  `41 C6 04 00 <imm>`, storing the action byte at the blob's size cursor and advancing it; the 23 action bytes
  were found by scanning `.text` for that pattern and each was attributed to a producer by the `lea` of its
  vtable. Evidence: `round4/D` §1.
- **F-054 · command strings** — `candidate` — `0x14348E990` "reroll_recruits", `0x14348E928`
  "super_reroll_recruits", `0x14348E980` "village_imgui", `0x14348E940` "check_reward_icon_visibility",
  `0x14348E960` "npc_notification_triggered"; the command dispatcher `0x14176A8E0` compares a std::string
  command against them. Evidence: `round4/D` §1.1/§2.
- **F-055 · widget fields the actions read** — `candidate` — sender context `widget+0x2E0` (the ctor
  `0x140521D00` zeroes `+0x2D0`/`+0x2D8`/`+0x2E0`); the numeric widget `+0x320` (ctor `0x140519A80`, class ctor
  `0x140503950`) whose u32 value `+0x2B4` is the amount for `0x18`/`0x19`; `+0x370`/`+0x371` the selected byte
  and its valid flag; `+0x1E8` gate bits 11 and 13. Evidence: `round4/D` §1.2.
- **F-056 · server's own action names** — `candidate` — `VillageModule.cs:69-101` names the actions (`0x00`
  reroll, `0x03` assign worker, `0x04` assign farm, `0x05` upgrade, `0x06` send shipment, `0x07` abandon,
  `0x0a` abandon captured, `0x0b` reroll port quota, `0x12` pause mapping, `0x13`/`0x14`/`0x15`/`0x16` take
  reward, `0x18`/`0x19` gold, `0x01`/`0x02` hire/release) and records that the action byte alone is ambiguous,
  so the dispatch key is (record kind, action). Evidence: `VillageModule.cs:69-101`.
- **F-057 · a captured gold action is little-endian** — `confirmed` — `0x18` was captured as
  `18 02 00 00 00`, i.e. action `0x18` and amount 2, not 33554432. Evidence: `round4/D` §0 (capture), live
  2026-11-23 (`F-086`).

## 7. What the panel shows and where its numbers come from (F-058–F-063)

- **F-058 · the panel reads a snapshot** — `candidate` — `0x140529970` calls the snapshot `0x1417690F0` at
  `0x1405299A6`, which copies 0x110 bytes from `record+0x190` and then calls `0x1424C4550` (time advance).
  Evidence: `round4/H` verdict, `round4/F` §6.
- **F-059 · snapshot layout** — `candidate` — `+0x00` clock, `+0x08` gold, `+0x0C` ETA base,
  `+0x10…+0x60` the 21 counters, `+0x64…+0x9C` the 15 production counters, `+0xA0…+0xA5` six flags, `+0xA6`
  onwards the pairs and blocks. Evidence: `round4/H`.
- **F-060 · wage formula** — `candidate` — `W = Σ over the 15 VillageProduction rows of J(job of that row) +
  Σ valid pairs J(kind 6, i) + Σ valid blocks J(kind 5, i) + J(kind 4, 0) (only when +0x29A != 0 and the
  +0x296 pair is valid)`, where `J(job) = Σ over worker rows whose +0xA8 name equals that job of the u16
  +0xA0 wage`. The panel prints the dword verbatim with localised unit text id `0x1DE0` at element `win+0x358`;
  the client adds nothing numeric. Evidence: `round4/F` §6, `round4/H`.
- **F-061 · ETA** — `candidate` — `(+0x198 * 3600 - +0x19C) / W` at `0x1424C3C25…0x1424C3C4D`; `+0x19C` is
  "gold already spent on wages" and gold and `3600·gold` share a unit. Evidence: `round4/F` §6.
- **F-062 · the time advance is a mini simulation** — `candidate` — `0x1424C4550`: elapsed 0 leaves the copy
  untouched (`0x1424C45C5 test rbx,rbx / je 0x1424C4DEE`); otherwise it clamps elapsed, rewrites the 15
  production counters into the copy, rewrites the three pairs, may reduce the copy's gold by the tick's wage,
  writes the copy's `+0xC` and advances the copy's clock. Evidence: `round4/F` §6.
- **F-063 · no worker rows means no wage** — `candidate` — with no worker rows every `J` is 0, so `W = 0` and
  the model sets `out+0x4BC = 1` ("no wage / no production"), leaving the wage and ETA empty.
  Evidence: `round4/F` §6.

## 8. Town objects and the doodad gate (F-064–F-072)

- **F-064 · doodad stage and room loop** — `candidate` — `0x142048e20` ("Generating doodads…", step `+0x58`)
  walks the subgraph records; its first step is every record's room doodads in placing order (`0x142046fc0`),
  reading `.arm` lines with `0x142a18700` (`x y [has position, x y] angle [quaternion] flip [wake group]
  has-fixed-height [height] [scale] "file.ao" "type"`). Evidence: `Agents/docs/re/011-level-generation.md:1304-1314`.
- **F-065 · placement** — `candidate` — `0x142035630` resolves the `.ao` with no random angle and filters
  `type+0x2ac ∈ {60, generator+0xd0}`, a per-line orientation mask (`+0x1e`) and the manager's vtable `+0x98`
  predicate `0x14152f9a0` (`type+0x2a8 ∈ {0,1}`); `0x142035a10` places the line with
  `cell = Orientation.Map(code, x, y, w*23, h*23) + room origin*23 + AOSet offset`, angle `2π − a` for codes
  4–7 plus `(code & 3)·π/2` plus the AOSet angle, scale = line × AOSet. Evidence:
  `Agents/docs/re/011-level-generation.md:1314-1318`.
- **F-066 · which types are created** — `candidate` — the object-type ctor `0x141ec7c10` takes from its
  BaseEvents `+0x2a4` (serialised, no `no_serialise`), `+0x2a8` 1 = `client_only`, 2 = `server_only`, and
  `+0x2ac` `league_category` (default 60, must be 60 or the generator's `+0xd0`); a line is created when its
  type is not serialised or client only, and not server only. Evidence:
  `Agents/docs/re/011-level-generation.md:1322-1326`, `Agents/docs/protocol/04-player-entity.md:95-98`.
- **F-067 · `type+0x80` is the component count** — `candidate` — the type ctor reads the `.ot` and sets
  `type+0x80` to the component count **before** the `client { }` blocks are appended; the read loop uses it.
  Evidence: `Agents/docs/protocol/04-player-entity.md:98`.
- **F-068 · the component-count gate** — `unknown` — the earlier notes said a room line is created only when
  that count is below 8, which is what keeps server-made objects off the interactive lines
  (`FarmingPlotInteractable`, `NoticeboardInteractable`, `MapDeviceButton`, the calendar NPC lines). Reason:
  this session's evidence files document where the count lives but not the comparison against 8, and that
  branch was not disassembled again; it is unreachable along the paths we read. Evidence: the previous version
  of `Agents/docs/game/settlers.md`, `Agents/docs/protocol/04-player-entity.md:98`.
- **F-069 · the doodad patch script** — `candidate` — `scripts/dev/kingsmarch-doodads.py` attaches with frida,
  reads `this.type.add(0x2a8).readU32()` (`:96`) and writes 1 once per type (`:124`); it hooks the room loop
  `0x142046fc0` (`:45`) and the per-line predicate `0x14152f9a0` reached through the manager's vtable `+0x98`
  (`:48`). Writing 1 also makes the creator skip the invisible-flag store `or byte [obj+0x1e6], 0x20`
  (`0x142048d59`), which the object's own reader `0x141e33ac2` checks. Evidence: `scripts/dev/kingsmarch-doodads.py`.
  Note: the previous settlers.md wrote the predicate as `0x152f9a0`; the script and the level-generation notes
  both give `0x14152f9a0`.
- **F-070 · the object-type register** — `candidate` — `0x2bb` resolves the type through
  `Game::GameObjectTypeRegister` at `world+0x8d8` (resource "Metadata/GameObjectRegister", factory
  `0x141eebd10`, cache lookup `0x141eea7d0`); the register is built locally from dat tables by `0x141ec8170`
  (row sizes 0x141, 0xac, 0xea, 0x264 …), as a sorted vector of 0x30-byte entries keyed by a u32 (lookup
  `0x141674420`, insert `0x1424fe270`), where the key is MurmurHash2 seed 0 over the path narrowed to bytes.
  A key missing from the register makes the world handler log "Unknown object type serialized by server" and
  create nothing; the `0x2bb` handler is `0x14218ddf0`, its reader `0x141fe6d40`. Evidence:
  `Agents/docs/protocol/04-player-entity.md:8-11,28-44`, `Agents/docs/protocol/address-index.md:108-109`.
- **F-071 · no HubTown type in the register** — `unknown` — the server's floor/building types are not in the
  register on this build, which is why a server-made copy fails with the log above. Reason: this comes from the
  live attempts and from the register being fed only by dat tables; the register's contents were not enumerated
  this session. Evidence: the previous `settlers.md`, `KingsmarchTown.cs:7-9`.
- **F-072 · the floor is client-drawn** — `candidate` — the town's floor, decks, wharf, `DecoUpgradeLarge`
  buildings, ships and farm plots are room doodad lines of `kingsmarch.arm` (room index 1984) that inherit
  `DecoUpgradeNoRender.ot` with `BaseEvents{server_only=true}`, so the client drops them; the room was counted
  earlier as 459 lines, 444 of them inheriting that type. Evidence: the previous `settlers.md`;
  `KingsmarchTown.cs:7-9,45`.

## 9. The server that implements the village (F-073–F-083)

- **F-073 · module constants and settlement** — `candidate` — `VillageModule.cs`: `ActionId =
  InstanceRulesets.ActionId` (`:24`), `TownRecordKind = 0x9b` and `FarmRecordKind = 0x74` (`:28`), the
  `Settlement` class with Gold, Workers, `Capacity = 90` (the client's `VillageBaseStats` row 20728),
  Assignments, Ranks, Recruits, Upgrades, Paused and Resources (`:32-63`). Evidence: `VillageModule.cs:24-63`.
- **F-074 · the three pushes** — `candidate` — `PushWorkers` sends subtag 0 (`:186-199`), `PushRecruits` subtag
  1 with the count emitted twice (`:205-218`), `PushState` subtag 4 (`:228-267`); `StateFrame` builds
  `02 c9 kind`, then a compact-uint length, then the subtag and payload (`:289-300`). Evidence:
  `VillageModule.cs:186-300`.
- **F-075 · dev files** — `candidate` — `data/kingsmarch-frames.txt` (`:112`), `data/kingsmarch-state.txt`
  (`:116`), `data/kingsmarch-recruits.txt` (`:120`), `data/kingsmarch-open-port.txt` (`:129`); `OpenPort`
  (`:134-154`) sends one `0x7b` with a default kind `0x58`. Evidence: `VillageModule.cs:109-154`.
- **F-076 · hire, assign, reroll, gold** — `candidate` — `Hire` (`:273-283`), assign (`:646`), reroll
  (`:511-515`), give gold (`:473-479`) and take gold (`:491-492`); `RecruitPrice = 2000` (`:125`) with the
  client's own `VillageRecruitBalancePerTier` holding 2,000 a tier. Evidence: `VillageModule.cs:122-125,273-283`.
- **F-077 · KingsmarchTown** — `candidate` — `data/kingsmarch-town.json` (`:23`), limit file
  `data/kingsmarch-limit.txt` (`:24`), the room origin `(138, 138)` (`:268`), the default filter
  `Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlot` (`:45`), the boards file
  `data/kingsmarch-boards.txt` (`:42`), record kinds and the `0x1f` path (`:67-75`), the village event names
  (`:77-80`), the `0x7b` shape (`:85-89`), and the farm replant (`:1105-1120`: `0x2bd` remove, then `0x2bb`
  create with the new plant state). Evidence: `KingsmarchTown.cs:23-45,67-89,1105-1120`.
- **F-078 · league flags for the town options** — `candidate` — owner flag 334 (`:57`), Fund the Treasury 1
  (`:63`), Invest in Recruitment 1434 (`:64`), Raulf's Town Recruitment 95 (`:65`); NPCTalk row 3610 "Manage
  Town" requires column-16 flag 334, row 3611 (its later variant) requires 1436 and refuses 334, row 3612
  "Fund the Treasury" requires 1, row 3613 "Invest in Recruitment" requires 1434, row 3556 "Town Recruitment"
  requires 95. Evidence: `KingsmarchTown.cs:47-65`.
- **F-079 · who the town NPCs are** — `candidate` — NPCs whose id starts `Metadata/NPC/League/Kalguur/` are the
  town's (`Modules/Core/Game/NPC/Npcs.cs:16`); the dialogue rows are NPCTalk 3606-3668
  (`Modules/Core/Game/World/KingsmarchDialogue.cs:10`); `TownNpcs` copies a town's NPC lines together with its
  room's code and origin. Evidence: `Npcs.cs:16`, `KingsmarchDialogue.cs:10`,
  `Agents/docs/protocol/17-npcs.md:83`.
- **F-080 · the eight village cells** — `candidate` — the Kingsmarch room's NPC lines: the King's Hand
  (429, 624), Tujen (283, 429), Rog (316, 523), Dannig (493, 478), Faustus (389, 625), Isla (213, 547), Sonja
  the Farmer (367, 794), Raulf the Recruiter (422, 664); `VillageGwennen` has no line in the room. These were
  read from the room data by an earlier session and recorded in the previous `settlers.md`; they were not
  re-read this session.
- **F-081 · the port** — `candidate` — `KingsmarchPort.cs`: `PortsFile = data/tables/VillageShippingPorts.json`
  (`:15`), per-port quota of six numbers (`:78`), `Gold` (`:242`), `PortList` (`:475`). Evidence:
  `KingsmarchPort.cs:15,78,242,475`.
- **F-082 · workers and wages** — `candidate` — `VillageWorkers.cs:10-11` the row layout, `:53` `Row`,
  `:68` `Recruit`; `VillageWages.cs:42` `Skill(rank) = clamp(rank - 1, 0, 4)` (five ranks), `:51`
  `Wage(jobType, skill)`. Evidence: `VillageWorkers.cs`, `VillageWages.cs`.
- **F-083 · town object list** — `candidate` — `data/kingsmarch-town.json`, from the client's `room_doodads`
  rows of room index 1984 (`kingsmarch.arm`): 444 rows `{type, model, x, y, angle}` plus the room origin;
  generated by `scripts/dev/kingsmarch_town.py` from `client.db`, written by `scripts/dev/extract-data.ps1`,
  not committed. Evidence: `KingsmarchTown.cs:11-12`, the previous `settlers.md`.

## 10. What the running client showed (F-084–F-092)

- **F-084 · the village UI is live** — `confirmed` — in a running client on 2026-11-23 the recruitment panel
  listed the town's workers with job, level and wage, and the candidate cards carried a price; the town panel
  opened and drew its rows. Evidence: live 2026-11-23 (the confidence legend of the previous `settlers.md`).
- **F-085 · reroll prices** — `confirmed` — live 2026-11-23: a reroll cost 2,000 gold, a high-quality reroll
  16,000, and a rerolled candidate was never below rank 3. Evidence: live 2026-11-23.
- **F-086 · hire and treasury gold** — `confirmed` — live 2026-11-23: hiring a candidate took the price out of
  the treasury, and giving treasury gold took the amount from the player's stash. Evidence: live 2026-11-23.
- **F-087 · the counters arrived as zero** — `confirmed` — live 2026-11-23: the 21 resource and 15 production
  counters showed zero and the total-wage line stayed empty; by F-058/F-060/F-063 that is what an all-zero
  subtag 4 plus no worker rows produces.
- **F-088 · Manage Town needed league flag 334** — `confirmed` — live 2026-11-20: NPCTalk row 3610 was not
  listed while bit 333 of the second flag set was clear (a read-only probe of `0x141E7BB90` logged `ret 0`,
  include id 334, `incSet [[334, 0]]`, while six plain rows of the same NPC returned 62); after the server sent
  `0x02bc` bit 3 with 334, the same row returned 62, "Manage Town" appeared in the dialogue (screenshot), a
  click sent C→S `0x82` option 4, and the server answered `0x7b 0x56`. Evidence: `C-trigger` §2/§4.
- **F-089 · an empty record crashed the client** — `confirmed` — live 2026-11-21 22:37: entering the town with
  an empty `0x1f` record faulted at `Game.patched.exe+0xdefc9a` seven seconds after the two `0x1f` messages.
  Evidence: `KingsmarchTown.cs:72-74`.
- **F-090 · the record list was empty while standing in town** — `confirmed` — live 2026-11-22, the measurement
  of F-005.
- **F-091 · subtag 9 crashed the client three times** — `confirmed` — live 2026-11-22, fault `0x1402D1029`,
  code `c0000005`; WER event 1001 at 11:49:49. See F-048.
- **F-092 · the floor needs the doodad patch** — `confirmed` — live: with the patch off the town's floor,
  decks, wharf and buildings do not exist; with it on they do. Evidence: `scripts/dev/live.ps1:14-17`,
  `Agents/status/world.md:111`.

## 11. Conflicts and open unknowns (F-093–F-098)

- **F-093 · what record 0x74 is** — `unknown` — `VillageModule.cs:28` calls 0x74 the farm record;
  `round4/I` §4.3 calls it the harbour/expedition record (its ctor wants `m_JobLevelCaps`). Both read the same
  ctor and vtable. Reason: the panel that owns the record was not identified this session.
- **F-094 · the fields of `0x1f`** — `unknown` — `round4/I` §0 gives `u32 kind | u8 value`;
  `KingsmarchTown.cs:68` and `VillageModule.cs:13` give `u8 kind | u32 value`. Reason: no capture of the actual
  bytes was found in the evidence files, and the factories ignore the value, so live behaviour cannot separate
  the two.
- **F-095 · the recruit-vector stride** — `unknown` — `round4/F` §3 says 0xB0 per element; `round4/I` §1 says
  0x10-byte elements for `+0x178`. Reason: only the reader-side behaviour was seen live (a working recruitment
  panel), and the stride was not re-read this session.
- **F-096 · the byte order of the 0x2c9 id** — `unknown` — `round4/I` §0 calls the u16 id big-endian; the
  captures show `02 c9` and the writer stores 0x2c9 as a word. Reason: whether the reader swaps the two bytes
  was not documented, and both readings produce the same two bytes for this id.
- **F-097 · the subtag-4 length** — `candidate` (resolved) — `round4/E` §3.2 reads the frame as 166 bytes
  (158-byte counters block + u64), while `round4/F` §4 and `round4/H` give 245 (`237 + ΣN + 8`) and
  `VillageModule.cs:238-264` builds 245 bytes of payload and works live. `round4/E`'s table enumerates only the
  front of the block; the 245-byte reading stands (F-044).
- **F-098 · where the ids dispatch** — `unknown` — the id → handler table for `0x1f`, `0x2c9` and `0x7b` was not
  found (`round4/I` §5: the table at `0x142150AE0` marks them unhandled and nothing references the candidate
  table). Reason: the `ClientVillage` vtable slot 8 path is documented, but which client code calls it for a
  given wire id is unreachable along the paths we read — so a bulk sync on some other id cannot be ruled out
  by this file.

## 12. Components and states of the town objects (F-099–F-100)

- **F-099 · components that consume no bytes** — `candidate` — `Brackets`, `Functions`, `Render`, `Preload` and
  `Targetable` are written with zero bytes, because their client readers are the `ret` family `0x1400f1180`
  (the same reader `Pathfinding` and `PlayerClass` use). Evidence: `KingsmarchTown.cs:255-259`,
  `Agents/docs/protocol/04-player-entity.md:109,114`.
- **F-100 · the visibility state** — `candidate` — `VillageVisibilityController`'s
  `on_or_create_state_visible_0` runs `DisableTargetable()`, `DisableHighlighting()` and `HideMinimapIcon()`,
  and `visible` starts at 0, so a server that wants the object visible sends 1 together with the state's
  "server value wins" flag (`0x02`), without which the client reverts the state to the type's default.
  Evidence: `KingsmarchTown.cs:34-37` (the states-file comment), the previous `settlers.md`.

## 13. Carried over from the previous note, not re-read (F-101–F-105)

- **F-101 · the previous note's "decision" address** — `unknown` — the earlier `settlers.md` named `0x142047330` as
  the decision the doodad patch works around and `0x152f9a0` as the hook. `Agents/docs/re/011-level-generation.md`
  §6c names the placement filter `0x142035630`, its per-line predicate `0x14152f9a0` and the "which types are created"
  branch at `0x1420481b0`, and `scripts/dev/kingsmarch-doodads.py:48` hooks `0x14152f9a0`; neither file mentions
  `0x142047330`. Addresses are therefore unreachable along the paths we read. Evidence: the previous `settlers.md`,
  `Agents/docs/re/011-level-generation.md:1302-1328`, `scripts/dev/kingsmarch-doodads.py:45,48`.
- **F-102 · four rebalance RVAs** — `unknown` — an earlier session recorded `0x148938`, `0x148ad3`, `0x148b38` and
  `0x1ec7701` as the crash frames in the client's `std::map` rebalance for a server-made copy of an interactive room
  line. No probe in this session reproduced it and the client was not run, so whether those four belong to that crash
  is unreachable along the paths we read. Evidence: the previous `settlers.md`.
- **F-103 · `ClientVillage+0xC40C`** — `unknown` — the previous note called `+0xC40C` the village's treasury field,
  zeroed only by the client constructor. The treasury the panel draws is the record's `+0x198` (F-026), and `+0xC40C`
  was not re-read this session, so its role is unreachable along the paths we read. Evidence: the previous
  `settlers.md`, `B-content/REPORT.md` §3.
- **F-104 · the two stash cells** — `unknown` — the previous note placed the stash and the guild stash on the room's
  own cells (420, 611) and (420, 641). The town's server-side object list is the farm plots and, optionally, the
  noticeboards (F-077), and the room data was not re-read for these two lines, so they are unreachable along the paths
  we read. Evidence: the previous `settlers.md`, `KingsmarchTown.cs:42-45`.
- **F-105 · `UnlockAll.cs`** — `unknown` — the previous note said `UnlockAll.cs` grants the NPCTalk gate flags a
  completed campaign would have set, and that `data/unlock-all.txt` makes the safe set automatic. The file was not
  re-read this session, so what it grants and whether it is still wired are unreachable along the paths we read.
  Evidence: the previous `settlers.md`, `Modules/Core/Game/World/UnlockAll.cs`.

---

## 14. Mining and smelting: the production rows, their rate and their counters (F-106–F-112)

- **F-106 · one line is one `VillageProduction` row** — `candidate` — the table has 15 rows of 60 bytes; a row's
  `Job` (`VillageJobs` row) is at `+0x2c`, its `BaseProductionTime` at `+0x28`, and the row array's element stride is
  `60 = 0x3c` (`0x1424c32e0` computes a row's index as `(row - begin) / 60`). The five `Mining*` rows eat
  `VillageResources` 0–4 and make 5–9; the five `Smelting*` rows eat 5–9 and make 15–19; the five `Farming*` rows
  make 10–14. Evidence: `0x1424c32e0` (the `(row - begin) / 60` index and `+0x28`/`+0x2c` reads),
  `sql/base/client/VillageProduction.sql` (the 15 rows and their Input/Output/BaseProductionTime).
- **F-107 · a row's remaining time, and the counter it reads** — `candidate` — `0x1424c5b80` returns
  `{u32 seconds, u8 valid}` for one ledger row: the row keeps the units made (`[0]`), the units wanted (`[2]`), the
  work done inside the current unit (`[3]`), `100 × BaseProductionTime` (`[0x28]`), the crew's efficiency (`[0x20]`,
  F-109) and the current unit's progress (`[0x2c]`), and the seconds fall out of
  `ceil((work · 100·T − progress) / efficiency)`, so one whole unit is `100 × BaseProductionTime` of counter. The
  value at `+0x2c` is what the settlement record's production counter carries and what the panel turns into its
  "Time" column. Evidence: `0x1424c5b80` (the `(v5*v7 - a1[11] + v6 - 1) / v6` line and the `a1[8]`/`a1[10]` reads).
- **F-108 · the snapshot advance consumes and produces** — `candidate` — `0x1424c4550` runs the ledger
  (`0x1424c32e0`), fills the ledger's per-row counters through `0x1424c4e20` → `0x1424c5250` (and that one's own
  call to `0x1424c96b0`, whose body was not read), then
  for each row with a chosen ore subtracts the amount made from the resource counter the snapshot keeps at
  `+0x10 + 4·k` and adds it to another (`sub [v31], v30` / `add [v34], v33` in the decompiled body), then moves
  the snapshot's clock by the elapsed seconds (`add qword [a1], elapsed`). With elapsed 0 it leaves the copy
  untouched. Evidence: `0x1424c4550` (decompiled body, the ledger loop and the clock write).
- **F-109 · a worker's efficiency is the skill-level row's second column** — `candidate` — `0x1424c2af0` takes the
  worker row and the job type, finds the `VillageJobSkillLevels` row keyed by the worker's skill byte
  (`movzx ecx,[rbx+0x10]`, the map at `[table+0x28]+0x20`), and returns `[row+4]` — the column the table names
  `PlayerLevelRequired`. `0x1424c2e20` accumulates `100 ×` that value per worker into the ledger row's `+0x20`.
  Evidence: `0x1424c2af0` (disassembly), `0x1424c2e20` (`+= 100 * v20`).
- **F-110 · the column holds the community's rank curve** — `confirmed` — the column is 12, 18, 27, 37, 49, 63 for
  job levels 0…5; against level 1 = 18 that is 0.67×, 1×, 1.5×, 2.05×, 2.72×, 3.5×, the curve the PoE Wiki states
  as "t2 1.5x, t3 2.05x, t4 2.72x, t5 3.5x". Evidence: `data/client.db` `VillageJobSkillLevels`,
  <https://www.poewiki.net/wiki/Kingsmarch> §Recruitment ("Worker efficiency").
- **F-111 · "Production Rate" is 3600 × Σefficiency / BaseProductionTime** — `candidate` —
  `0x141769b20`'s mining/smelting branch returns `3600 × ledger[row][+0x20] / (100 × [productionRow+0x28])`, and the
  ledger value is already `100 ×` the summed efficiency (F-108, F-109), so the two hundreds cancel. One rank-1
  worker on Crimson Iron (T = 120) therefore reads 540/h, not the 30/h the earlier note assumed from efficiency
  "1.0 ×100": that assumption took the worker's `+0x20` for a fixed point rather than the skill-level column.
  Evidence: `0x141769b20` (`v4 = 3600 * v7 / (100 * [v13+40])`), F-109.
- **F-112 · an idle ore type falls back to another** — `confirmed` — the mining and smelting rows work their own
  ore, and when the town holds none of it the crew works another ore the town does hold: the PoE Wiki's mining and
  smelting sections say "If no marked ore deposits that are designated to a Miner are available, they will
  automatically mine another available ore type" (the smelters' sentence reads the same way), and the client's
  ledger carries the chosen ore at `dx[+0x30]` with its "chosen" flag at `dx[+0x34]` (`0x1424c3fb0`). Evidence:
  <https://www.poewiki.net/wiki/Kingsmarch> §Mining/§Smelting, `0x1424c3fb0`.

## 15. The ore deposits' encounters (F-113–F-116)

- **F-113 · the client's own encounter monsters exist and load** — `confirmed` — nine varieties the five deposits' fights
  are made of are in `MonsterVarieties` and `Monsters.Find` resolves every one of them with `Supported = true`
  (probed 2026-11-24 against `data/client.db` with the Game module's data loaded):
  `LeagueHellscape/FleshFaction/HellscapeFleshFodder1` (the Flesh Beyond the wiki names),
  `LeagueKalguur/CrimsonIron/SmallGrowthMaps` (the Corrupted Growths),
  `LeagueKalguur/DemonFaction/DemonCopperDemonFodder1_` and `LeagueKalguur/Daemon/CopperHeraldOfSkyfalllDaemon` (the
  Orichalcum altar's demons and its falling meteors' herald),
  `LeagueKalguur/PetrifiedWood/PetrifiedWoodFungusZombie_`, `…/PetrifiedWoodPrimordialMonster1` and
  `…/PetrifiedWoodPunchingBag` (the amber's pack and the amber itself),
  `LeagueKalguur/VerisiumGuardBoss/VerisiumGuardBoss` and `…/VerisiumGuardDaemon`.
  Evidence: `data/client.db` `MonsterVarieties` rows 10341/10342/10345–10362 and 9362, the probe's output in the pull
  request description.
- **F-114 · what each deposit asks for** — `confirmed` — the wiki's ore-deposit page describes five mechanics: Crimson
  Iron blooms Corrupted Growths that spawn Flesh Beyond monsters and needs all the Growths killed; Orichalcum summons
  Pale or Demonic Beyond around an altar while meteors fall; Petrified Amber must survive the pack it attracts, and the
  ore is the durability left when the pack is dead; Bismuth only adds elemental monster modifiers (no encounter);
  Verisium is channelled, which scales its monsters and its ore. Evidence:
  <https://www.poewiki.net/wiki/Ore_deposit> (read 2026-11-24).
- **F-115 · the encounter buffs' rows** — `confirmed` — `client.db` keeps buffs in `BuffDefinitions` (996 tables in all;
  an earlier reading of this file looked for a table named `Buffs` and wrongly concluded there was none). The page's
  buffs are rows **2491** `demon_copper_meteors` ("K'Tash's Anger", 30 s, `Removable = 0`), **2492**
  `demon_copper_lightning` ("Beidat's Wrath") and **2718** `verisium_channel_shield` ("Verisium Energy", its
  description "You cannot be Stunned or Damaged while channelling Verisium"); the Orichalcum pair carries the buff
  visuals 2159/2160, so the rows are complete and a server can send them. **Not found**: the Petrified Amber's buff
  ("Amber Blood"): no `BuffDefinitions` row has `amber` in its id or name, and none of the rows whose ids or
  descriptions mention gold or duplicates is a Settlers buff, and no row is named "Verisium Infusion" either (the only
  Verisium buff is 2718, the channelling shield; the only two `infusion` ids are 2374 and 2797, neither a Settlers row).
  Reason it stays unknown: the buffs' in-game names are the only handles we have and they match no row.

### What the server now sends (F-121)

- **F-121 · the Orichalcum blessing goes out as `0x1c1`** — `confirmed` — `InstanceEntities.AddBuff` over rows 2491 and
  2492 with `u68 = 0x41F00000` (30.0 s) is 31 bytes: `01 C1 | u32 object | u32 0 | u16 0 | u16 instance | u16 row
  (09BB/09BC) | u32 source | u16 skill | u8 flags 0x02 | u32 seconds`. The row is the whole payload the client needs:
  the buff's visual is the row's own (`BuffVisualsKey` 2160/2159), so no `visual` field is written. Row 2491 =
  `0x09BB`, 2492 = `0x09BC`, 2718 = `0x0A9E`, measured 2026-11-24.
- **F-121b · the Pale/Demon split is three varieties, not one** — `confirmed` — the altar's faction is a variety family in
  `MonsterVarieties`: Pale is `LeagueKalguur/PaleFaction/DemonCopperPaleFodder1..3` (rows 10358–10360) with
  `Daemon/CopperHeraldOfSkyfalllDaemon` (9629), Demon is `DemonFaction/DemonCopperDemonFodder1_..3` (10353–10355) with
  `Daemon/CopperHeraldOfThunderDaemon_` (9628). All four resolve `Supported = true` through `Monsters.Find`. The heralds
  are the page's two sky effects: thunder for Beidat's Wrath (Demonic), skyfall for K'Tash's Anger (Pale).
- **F-116 · the amber's durability is the server's own number** — `candidate` — the wiki says each hit takes durability,
  with stronger or closer enemies taking more, and no client table carries the value; the server therefore keeps a
  percentage that falls while the pack is alive (4 points a second) and pays that share of the deposit when the pack
  dies. Evidence: `Modules/Core/Village/KingsmarchOre.cs` (`AmberBite`), F-114.

## 16. The ore nodes' state machines in the client (F-117–F-120)

- **F-117 · the five nodes' object-type hashes** — `confirmed` — `InstanceEntities.ObjectTypeHash` (the client's own
  type hash the object messages carry) gives, for the node paths: Verisium `0x89138fcc` (−1995206708), Crimson Iron
  `0xb833c59a` (−1204566630), DemonCopper `0x7c6422af`, Petrified Wood `0xcfd5ef4a` (−808063158), Bismuth
  `0x4edb4b29`, Hourglass `0x3aa63331`. Computed 2026-11-24 with the Gateway's own function over the five paths.
- **F-118 · the client's state gates are keyed by those hashes** — `confirmed` — three client functions test an object's
  type hash against literals and then consult the object's own state list (a vector of 192-byte entries at
  `[entity+0x158]+0x10`): `sub_140CA32D0` (hash −1995206708 = Verisium, and the "begun_channelling" literal at
  `0x1431E61D0`), `sub_140CA49E0` (the same literal), `sub_140CA6EE0` (hash −808063158 = Petrified Wood,
  "activated"). The state *names* come from the data (`object_type_shared_states`); the code only gates transitions.
  Evidence: the decompiled bodies, the hash constants, F-117.
- **F-119 · what the Verisium gate decides** — `candidate` — in `sub_140CA32D0`'s Verisium branch: the object's state
  list is searched for `begun_channelling`; when it is absent the predicate answers 1; when it is present it reads
  `encounter` and answers **0 while `encounter > 0`** (the fight is running: no interaction), and otherwise answers
  **2 when `begun_channelling != 1`** and **1 when `begun_channelling == 1`**. Read as one design: the server owns both
  states — it sets `encounter` while the deposit's encounter runs and `begun_channelling` while a player channels — and
  the client only decides whether to offer the interaction. Evidence: `sub_140CA32D0` (the `sub_141E43830`
  `encounter`/`begun_channelling` reads and the `v4 + 1` return).
- **F-120 · the messages a held interaction can use** — `candidate` — the client's object use is C→S `0x24`
  (`UseSkillOnObject`: u32 target, u16 skill, u16 sequence, u16 flags) and a held action is reported by C→S `0x197` /
  `0x198` (10 bytes: u32 BE x, u32 BE y) with C→S `0x199` (2 bytes) ending it; both were read off live captures
  (`InstanceProtocol.ChannelUpdateId`/`ChannelEndId`, `Agents/docs/protocol/c2s.md:266`). Whether a Verisium deposit is
  channelled through those ids or through repeated `0x24` is **unknown**: no capture of a deposit channel exists, and
  static code cannot say which path the client's interaction takes. Reason it is unknown: the deposit's interaction
  lives in its `.ot` data (`InteractionAction`), and that file is not in `data/game-files.zip`.

## 17. The five node types' state names (F-122)

- **F-122 · each node type's state list, from `object_type_shared_states`** — `confirmed` — the states the client reads
  for a node are its type's rows in `object_type_shared_states`, in row order. All five declare **`activated`,
  `in_progress` and `ore_empty`**; the rest are the type's own:

  | Node | states (row order) | components after `InteractionAction` |
  |---|---|---|
  | Verisium | activated, in_progress, ore_empty, encounter, begun_channelling, channelling, in_zone | StateMachine, ControlZone |
  | Crimson Iron | activated, in_progress, ore_empty, interactable, growths_alive | StateMachine, ControlZone, Functions |
  | Demon Copper | activated, in_progress, ore_empty, encounter, type, lights | StateMachine, ControlZone |
  | Petrified Wood | activated, in_progress, ore_empty, am_dead | StateMachine, ControlZone, TriggerableBlockage |
  | Bismuth | activated, in_progress, ore_empty, effect_off | StateMachine, ControlZone, Buffs, Functions |

  Evidence: `object_type_shared_states` through the server's own `ObjectTypes.StateMachine`, printed 2026-11-24.
  This answers the question the string scan left open (F-118): the names are data, and the `.ot` bodies the code gates
  on are `begun_channelling` (Verisium) and `activated` (Petrified Wood).
- **F-122b · what the names say about each fight, and what the server now sets** — `candidate` — `in_progress` is the
  one state every type declares, so it is the honest "its fight is running": Verisium's `encounter` and Demon Copper's
  `encounter` are the Verisium gate's own input (F-119) and the altar's; Demon Copper's `type` and `lights` are the
  Beyond faction and its sky effect; Crimson Iron's `growths_alive` and `interactable` are its Growths; Petrified
  Wood's `am_dead` is the amber's death — the state a lost deposit should end in. The server sets `activated` when a
  deposit is touched, `in_progress` while its fight runs and `ore_empty` once it is spent, and leaves the type-specific
  ones alone until each is established by a capture.
- **F-122c · a node written by hand, for the capture** — `confirmed` — a Verisium node at cell (120, 120) with every
  state 0 is a 56-byte `0x2bb` the client accepts:
  `02BB 0D5000FF 00000000 00000000 89138FCC 0026 0000 78000000 78000000 … 07 00000000 00000000 …`
  (object id, u32 0, u32 0, type hash, component byte count 0x26, then the component blob whose StateMachine is
  `07` states and 7 zero flags). Object type hashes and the other four types' lengths: Crimson Iron 52 B
  (`0xb833c59a`, 5 states), Demon Copper 54 B (`0x7c6422af`, 6), Petrified Wood 51 B (`0xcfd5ef4a`, 4), Bismuth 52 B
  (`0x4edb4b29`, 4). This is what lets a capture put a node in front of the player without a map.

## 18. The client only takes an ore node placed by the area load (F-123–F-124)

- **F-123 · a node sent after the area load draws, names itself and refuses the click** — `confirmed` — a Verisium node
  written into the live area (`0x2bd` + `0x2bb` + wake `0x1a9`, the same bytes `Ensure` sends for a map's own deposit)
  renders with its name plate and the "Click and hold to channel energy into the Verisium Outcrop" tooltip, and the
  client's cursor resolver (`0x1401285b0`) runs on it, yet **C→S `0x24` is never written**: the writer at
  `0x141f983f0` stayed unhooked-uncalled across a minute of clicking (Frida, 2026-11-24). The same node placed by the
  area load — `Ensure` from `AreaGenerated`, forced to a count with `/villagepanel force 2` — is clicked and
  `GameEvents.ObjectUse` runs. So the refusal is about *when* the object arrives, not about its bytes: the area's own
  object table is what the interaction path consults, and a late create is not in it.
- **F-124 · the Verisium state gate is not the blocker** — `confirmed` — `sub_140CA32D0` (the type-hash gate of F-118)
  answers **2** on that node every frame while the cursor is over it: 2 is its "no `begun_channelling`, interaction may
  start" value (F-119). Read with F-123: the gate passes, the interaction still does not start, so the gate is a filter
  the interaction applies after it has decided the object is usable at all.
- **F-125 · spawning the Kalguur encounter monsters crashes the client** — `candidate` — with
  `Game_ORE_ENCOUNTERS=all` the Petrified Amber's pack (8 × `PetrifiedWoodFungusZombie_`, 4 ×
  `PetrifiedWoodPrimordialMonster1`, plus `PetrifiedWoodPunchingBag` as the amber) is created (0x2bb blobs of 128–242
  bytes, all accepted) and fights for ~20 s, then the client dies: WER `APPCRASH` `Game.patched.exe`, faulting module
  KERNELBASE.dll, `P7 = e0000001` (a raised unhandled exception, the client's own `throw`), at 2026-11-24 19:16:36. The
  last packets before it are the pack's repeated `0x1ab` actions. Which variety throws is not established: the server
  keeps unproven varieties behind `Game_ORE_ENCOUNTERS=all` (`KingsmarchOre.Proven`) until each one is proven one at a
  time.

## 19. How the client decides a Verisium click is a channel (F-126–F-129)

Read out of `sub_140CA49E0` (the interaction dispatcher) and `sub_140CA32D0` (the state gate) after a live click on a
node that rendered, named itself, passed the gate and still sent nothing. All four answers are static; no live
experiment was needed once the dispatcher was read.

- **F-126 · the node must carry `begun_channelling` or the click is dropped in silence** — `confirmed` — the
  dispatcher's Verisium branch (`type hash -1995206708`) first looks `begun_channelling` up in the node's own state
  list and, when the state is **absent**, returns without doing anything at all. Nothing is sent, nothing is shown. This
  is exactly the "clicked it, no reaction" symptom, and it is a *state-name* requirement, not a timing or byte one. Our
  writers only ever sent `activated` / `in_progress` / `ore_empty`, so a Verisium node was a node the client could not
  act on:
  ```c
  v36 = (index of "begun_channelling" in node->states) == node->states.size;   // true = absent
  if (v36) return;                                                           // silent
  ```
  What follows only runs when the state **exists**:
  ```c
  v48 = value_of(node, "begun_channelling");
  if (gate != 0 && (skillId == 23585) == (v48 != 0)) relevance();             // 23585 = the channel skill
  ```
  So the client reads the channel as a **toggle**: clicking with `begun_channelling == 0` and the channel skill starts it,
  clicking again with it non-zero stops it. The server owns the value; the client only compares.
- **F-127 · there is no separate C→S channel message** — `confirmed` — `0x197`/`0x198`/`0x199` are written by the
  *generic* writers `0x141f98f90` and `0x140911160` (shared with seven and fifteen other messages), not by anything
  ore-specific, and no ore code path references them. A channel is an ordinary interaction (`0x24`) repeated, distinguished
  only by `skillId == 23585`, plus the state the server keeps.
- **F-128 · the channel length is a server stat, not a client rule** — `confirmed` — `map_settlers_verisium_maximum_channel_+%`
  (Stats row **21195**) is a `map_`-prefixed mod: the area's own mods carry it, so the maximum is computed where the area's
  stats are (the server). The client's Verisium branch reads state values and the skill id and never reads a duration.
- **F-129 · the yield is the server's too** — `confirmed` — the branch above ends in `relevance()` (the object is queued
  for the interaction) and nothing in it computes an amount, spawns anything or rolls a table. Everything the wiki
  describes after the channel (the ore, the wardens, the drops) happens server-side.
- **F-130 · the five node types' complete shared-state lists** — `confirmed` —
  | rank | folder | shared states |
  |---|---|---|
  | 1 | `CrimsonIron` | `activated, in_progress, ore_empty, interactable, growths_alive` |
  | 2 | `DemonCopper` | `activated, in_progress, ore_empty, encounter, type, lights` |
  | 3 | `PetrifiedWood` | `activated, in_progress, ore_empty, am_dead` |
  | 4 | `Bismuth` | `activated, in_progress, ore_empty, effect_off` |
  | 5 | `Verisium` | `activated, in_progress, ore_empty, encounter, begun_channelling, channelling, in_zone` |
  Read from `object_type_shared_states` by the type's row id (`object_types.rowid`), not by `object_types._index` (that
  column is literally the string `_index`). A type's `shared_timers` is `[]` for all five, so the state machine carries no
  timer: the values are pure integers the server sets.

## 20. A click reaches the server, and the two "hold" ores never send one (F-131–F-133)

- **F-131 · the click chain, confirmed from disassembly** — `confirmed` — a left-click on a world object runs
  `0x14011ECB0` (input dispatcher, `case 6`) → `0x1421660B0` (use a skill) → `0x141E94010` (build the action: template
  lookup `0x141EF0050` by HASH16, target binding `0x1419437F0` into `action+0x128`) → `0x141E937B0` (start gate) →
  `0x141E94900` (commit) → slot 17 of the game object `0x14216CCB0` → the 12-byte writer `0x141F983F0`. All four
  variable fields of C→S 0x24 come from the **action**: the target is `*(u32*)(boundTarget+0x88)`, the skill is
  `*(u16*)(*(action+0xF0)+0x40)`, the sequence is `action+0x11A` and the flags are `action+0x122`.
- **F-132 · the object dispatcher is not on that path** — `confirmed` — `sub_140CA49E0` (and the state gate
  `sub_140CA32D0` it calls) is the **hover/preview** pass: its only two exits append the object to the highlight lists
  or call `0x140CA57F0`, a ray-projected highlight-position update that never touches `0x141E94010` or the network.
  Everything in §19 about that gate is true of the hover path and says nothing about whether a click is sent. The
  start gate `0x141E937B0` is unreachable from it for **any** object type.
- **F-133 · two of the five ores never send a click** — `confirmed` — with one deposit of each type in one area, the
  click produces C→S `0x24` for Crimson Iron, Petrified Amber and Bismuth (repeatedly, as often as the player clicks),
  and **not once** for Orichalcum or Verisium. Those are exactly the two whose client description reads "click and
  hold"; the other three read "interact". So the difference is the *kind of interaction* the client offers for the
  type, and whatever makes a held interaction start an action is not established.

## COVERAGE STATEMENT (fact layer)

**Examined — client functions.** `0x140529450` (town map Show), `0x1405235c0` (window ctor), `0x140521d00`
(widget ctor), `0x140523520` (row fill), `0x14052b100` (observer), `0x140529970` (panel reader),
`0x1417690F0` (snapshot), `0x1424C4550` (time advance), `0x1424C8190` (subtag-4 reader), `0x141767F60` (kind-0x9B
handler), `0x14168A080` (kind-0x74 handler), `0x141FA7CA0` (0x2c9 reader), `0x141FA7A10` (0x2CA ctor),
`0x1421A1960` (0x2CA send), `0x14176A8E0` (village command dispatcher), `0x1402d0a90` (subtag-9 widget),
`0x1424C32E0` (wage model), `0x1424C7F80`/`0x1424C7CA0`/`0x1424C9080`/`0x1417737D0` (worker-row readers),
`0x141773300`/`0x1417734C0` (recruit reader), `0x141E7BB90` (NPCTalk row availability),
`0x14012AE40` (second flag set), `0x1418cc180` (container ctor), `0x140363210` (container factory),
`0x1415A65D0`/`0x1415a6800` (ClientVillage and its slot 8), `0x141767BA0`/`0x141687370` (record ctors),
`0x1416563B0`/`0x140D57090` (InstanceDetails and its only creator), `0x142048e20` (doodad stage),
`0x142046fc0` (room doodad loop), `0x142035630`/`0x142035a10` (doodad filter and placement),
`0x14152f9a0` (per-line predicate), `0x141ec7c10` (object-type ctor), `0x141674420`/`0x141ec8170` (object-type
register), `0x14218ddf0`/`0x141fe6d40` (0x2bb), `0x142a18700` (`.arm` line reader), `0x1400ECFF0` (stand-in
predicate), `0x1400f1180` (no-op component reader).

**Examined — message ids.** `0x1f` (record creation), `0x2c9` (state channel; subtags 0, 1, 2, 3, 4, 5, 6, 7, 8,
9, 0xA, 0xB, 0xC, 0xD), `0x2ca` (action channel; actions `0x00`–`0x07`, `0x0A`–`0x1D`), `0x7b` (panel open),
`0x82` (NPC option), `0x84` (NPC talk/close), `0x2bb` (create object), `0x2bc` (quest flags / league quest
flags), `0x2bd` (remove object), `0x2aa` (gold), `0x85` (dialogue closed).

**Examined — how a click becomes a channel (F-126–F-130).** Decompiled `sub_140CA49E0` (the interaction dispatcher) and
`sub_140CA32D0` (the gate) in full; read `object_types` / `object_type_shared_states` / `Stats` from `client.db`; checked
which writers own `0x197`/`0x198`/`0x199` in the C→S table. Not examined: what the server should do while
`channelling` is set (the ore roll, the wardens, the completion), because the client does none of it and no client data
says what it is.

**Examined — whether the client accepts a node (F-123–F-125).** Frida hooks on the C→S `0x24` writer `0x141f983f0`,
the cursor's target resolver `0x1401285b0` and the Verisium gate `sub_140CA32D0` during live clicks on a node; the
Gateway's own packet trace of the create and the click; the Windows event log's crash report. Not examined: which
Kalguur variety raises the exception (F-125), because proving it means spawning one per client run.

**Examined — the node state names (F-122–F-122c).** `object_type_shared_states` for the five node types through
`ObjectTypes.StateMachine` (all five lists, in row order), and the `0x2bb` create byte-for-byte for each of them. Not
examined: the `.ot` bodies behind those states (still absent from `data/game-files.zip`), so what each type-specific
state *does* on the client is unknown until a capture shows it.

**Examined — the nodes' state machines (F-117–F-120).** Client functions `sub_140CA32D0`, `sub_140CA49E0`,
`sub_140CA6EE0` (the three object-type state gates), the string `0x1431E61D0` ("begun_channelling") and its six code
references, `InstanceEntities.ObjectTypeHash` over the five node paths, `BuffDefinitions` rows 2491/2492/2718 and their
neighbours. Not examined: the `.ot` files (not in `data/game-files.zip`), so the channel's duration and the interaction's
own parameters are unknown; no capture of a deposit channel or of an encounter buff being applied exists.

**Examined — the deposits' encounters (F-113–F-116).** `MonsterVarieties` rows for the five deposits' own monsters
(probed: all nine supported), `object_types` rows 10478–10494 (the five node types, their SmallGrowth/CopperSpawner/
FungalSpawner/copper_altar objects and their read lists), the wiki's ore-deposit page. Not examined: the buff rows
(no `Buffs` table in client.db, F-115), the Growths' own spawn cadence, and the Verisium channelling input.

**Examined — the production half (F-106–F-112).** Client functions `0x1424c5b80` (a row's remaining time),
`0x1424c4550` (the snapshot's advance), `0x1424c5250` (the counters the advance writes), `0x1424c32e0` (the
ledger and the wage), `0x1424c2e20` (the crew's efficiency), `0x1424c2af0` (the skill-level column), `0x141769b20`
(the rate the panel prints), `0x1424c3fb0` (the chosen ore), `0x1424c5870`/`0x1424c6440` (a row's units and its
availability), `0x1424c8190` (the subtag-4 reader); tables `VillageProduction` (15 rows), `VillageResources` (21),
`VillageJobs` (31), `VillageJobTypes` (10), `VillageJobSkillLevels` (6), `VillageBaseStats` (30). Not examined: the
`VillageUpgrades` table (absent from client.db), and whether the client's advance writes the record's own counters
back or only the copy's (F-108 examined the copy only).

**Examined — tables.** `VillageJobs` (31 rows), `VillageResources` (21 rows), `VillageProduction`,
`VillageJobTypes`, `VillageFarmAdjacency`, `VillageBaseStats` (row 20728 = 90), `VillageUpgrades`,
`VillageShippingPorts`, `room_doodads` (room 1984), `room_point_rooms`, `NPCTalk` (rows 3556, 3606–3668, 3610,
3611, 3612, 3613), `NPCs`.

**Examined — runtime probes.** The read-only frida probes in `Tools\re\out\kingsmarch\`:
`C-trigger\probe-manage-town.py` (NPCTalk row availability, `probe2.log`/`probe5.log`),
`A-frame\hookrecords.py`/`village.py`/`townmap.py`/`windows.py`/`rows.py` (pointer chain, record list, window
rows), `B-content\peek.py`/`bandscan.py` (container and widget fields), `ce\frida-villageevent.py` /
`frida-screens.py` / `frida-slots.py` (record and UI events), `send-frames.py` (one frame at a time), plus the
live sessions of 2026-11-20 (dialogue row and `0x7b`), 2026-11-21 (record creation), 2026-11-22 (record list,
counting probe, subtag 9) and 2026-11-23 (panel numbers, reroll, hire, treasury).

**Not examined — separately, and never to be read as "no problem".**

* Client functions: the real wire-id dispatcher (if it is not `0x142150AE0`), the per-id handler entry for
  `0x1f`/`0x2c9`/`0x7b`, the comparison against the component count (F-068), the reader that builds the port
  panel's pin board `[ClientVillage+0x5C8]`, the encrypted keyed table at `supplier+0x168`, the two base
  vtables `+0x18`/`+0x158` (F-037), and every draw path of the `0x74` record beyond the null-check hazard.
* Fields never read by a path we read: `+0x150`, `+0x2A8`, `+0x3E0` triples, `+0x4D0` (F-036).
* Message ids: `0x1e` (bulk record sync), `0x2c8`, `0x2cb`, `0x7c`, `0x7d`, and every other instance id that
  the server does not send for Kingsmarch. `0x1e` matters most: if it carries record contents, the
  "only `0x2c9` mutates the record" statement in the judgement layer would need re-checking.
* `0x2ca` actions `0x08` and `0x09` (not found in the family), `0x1e`, `0x1f`, `0x1a`.
* Tables: `VillageJobLevelCaps`/`m_JobLevelCaps` (id `0x86A`) contents, the client's `VillageRecruitBalancePerTier`
  rows, `VillageShippingFailures`, the encrypted keyed table.
* The object-type register's actual contents (F-071) — not enumerated.
* The doodad component-count branch (F-068) — not disassembled this session.
* Addresses and numbers carried from the previous `settlers.md` and not re-read: `0x142047330`, `0x152f9a0`, the four
  rebalance RVAs `0x148938`/`0x148ad3`/`0x148b38`/`0x1ec7701`, `ClientVillage+0xC40C`, the two stash cells (420, 611)
  and (420, 641), and `UnlockAll.cs`/`data/unlock-all.txt` (F-101–F-105).
* Runtime probes not run this session: no client was started, no frame was re-sent, no live session of
  2026-11-23 was repeated for this document.