# Reverse-engineering tooling

What was used to map the client, how to run each tool and what it is good for. Paths are relative to the repo root.
The client binary is `Client/Game.exe` (79 MB, image base `0x140000000`). `Client/Game.patched.exe` is the same
apart from the login key blobs, so addresses are the same in both.

## 1. Ghidra (headless, read-only)

For the frozen projectile study, start with [RE 012](012-projectile-execution.md) and the
[offline delivery checks](../../../scripts/re/projectiles/README.md).
Those checks need neither the client nor Ghidra; historical instruction-audit receipts are not public native replays.

A fully analyzed project already exists: `Tools/ghidra-projects`, project `poe3293`, program `PathOfExile.exe` (the
same binary as `Client/Game.exe`). Do not rename the project or program, because the scripts use those names. REA's
own Ghidra import of the 79 MB file exceeds its 1-hour limit, so use this project instead of re-analyzing.

Runner: `bash scripts/re/ghidra/g.sh <Script.java> <args>` (about 12–15 s per call, `-noanalysis -readOnly`). It sets
`JAVA_HOME` to `Tools/jdk-21`, takes scripts from its own directory (so a worktree's scripts run as they are) and prints
only ERROR/Exception/SCRIPT lines (`GLINES=<n>` for more). Arguments go through `cmd.exe`, which splits at `,` and `=`. Output paths must be Windows paths
(`C:\…\Tools\re\out\x.c`). PowerShell variants: `scripts/re/ghidra/decomp.ps1`, `run.ps1`.

| Script | Usage | Use it for |
|---|---|---|
| `Decomp.java` | `<out> <addr[:depth]>…` | decompile the function containing each address, with `:N` also its callees to depth N |
| `DecompAll.java` | `<outdir> [threads] [limit] [indexonly]` | the full dump below (every function + `index.tsv`) |
| `Range.java` | range in `Tools/re/out/range.txt` ("lo hi") → `range.c` | decompile every function in an address range (class methods sit together) |
| `Callers.java` | `<out> <symbol\|0xaddr>…` | every function that references a symbol (import, thunks followed) or an address, data refs included. Used for imports (`connect`, `send`), strings, pools, setters |
| `FindSenders.java` | `<out> <disp>…` | every function using a displacement, with the ids passed to `htons`. Found login senders (`0xdd0`) and state writers (`0xc3a0`) |
| `FindHtons.java` | `<out> <id>…` | functions calling `htons(<id>)`. Found nothing for instance messages, whose ids are stored in the message object |
| `RefsIn.java` | `<out> <start:len>…` | references into a range: who installs a vtable or touches a blob |
| `Refs.java` | names or addresses in `Tools/re/out/rx.txt` | functions referencing each |
| `Syms.java` | `<out> <regex>` | symbols matching a regex |
| `ComponentRegistry.java` | `<out.json>` | the component registry (section 7) |
| `ReadSchema.java` | `<out.json> <components.json> [factory:a+b] [only:f+g] [withc]` | read-sequence summaries (section 7) |
| `SendSites.java` | `<out.json> [send:0xaddr/REG+…]` | every call of a send function (default the instance send `0x1421752b0`, message in RDX) resolved over the CFG to its (vtable, id) pairs, path by path (section 7) |

### Full decompilation dump (grep before you run Ghidra)

`Tools/re/out/decomp-all/` (git-ignored, never commit it) holds the whole client decompiled, one file per function:
`<hex digits 3–5 of the VA>/FUN_<addr>.c`, e.g. `0x1e5123450` → `1e5/FUN_1e5123450.c`. Each file starts with
`// ===== <name> @ <addr> =====`; a function the decompiler gave up on (60 s timeout) has `// decompile failed: …`
instead of code. `index.tsv` has one row per non-thunk function: `addr size name callers status strings`, where
`callers` counts calling functions and `strings` lists the ASCII/UTF-16 strings the function references (directly or
through one pointer), separated by ` | `. `progress.log` records each run (counts, rate, failures).

Regenerate (about 1 h on 4 threads; resumable, it skips files that exist, so rerun the same command after a crash):

```bash
JAVA_HOME="$PWD/Tools/jdk-21" GHIDRA_HEADLESS_MAXMEM=4G cmd //c "Tools\ghidra_12.1.4_PUBLIC_20260921\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat Tools\ghidra-projects poe3293 -process PathOfExile.exe -noanalysis -readOnly -scriptPath scripts\re\ghidra -postScript DecompAll.java C:\…\Tools\re\out\decomp-all 4"
```

Arguments: `<outdir> [threads] [limit] [indexonly]` (`limit` stops after N functions for a test; `indexonly` only
rebuilds `index.tsv`). Delete the directory first to redo everything.

The full run (111,248 functions, 183 thunks excluded) took 52 min on 4 threads and 4 GB of heap; 14 functions failed
(12 timeouts on 12–95 KB functions; `0x140f63d50` crashed the decompiler, `0x14136ba90` hit "Flow exceeded maximum
allowable instructions"). Search the `.c` files with ripgrep (the agent Grep tool,
seconds); `grep -r` from Git Bash over 111k files takes minutes. `index.tsv` is small enough for plain `grep`/`awk`.

Example searches (from the repo root, `D=Tools/re/out/decomp-all`; for the `.c` files the same patterns work in Grep/`rg`):

```bash
grep -F 'Metadata/Terrain' $D/index.tsv | cut -f1,3,6          # functions referencing a string (ASCII or UTF-16)
grep -rlF '+ 0x2b4) =' $D --include=*.c                        # writers of a field at displacement 0x2b4
grep -rF 'FUN_1410e00bd(' $D -l                                 # callers of a function (calls in the C text)
cat $D/141/FUN_1410e00bd.c                                      # one function, no Ghidra run
awk -F'\t' '$5=="failed"' $D/index.tsv                          # functions to decompile by hand (Decomp.java, longer timeout)
sort -t$'\t' -k4,4nr $D/index.tsv | head                       # most-called functions
```

The decompiler sometimes hides field writes in casts (`*(undefined4 *)(param_1 + 0x2b4)` vs `param_1[0xad]`), so
grep both forms or fall back to `finddisp.py` for the instruction level.

Earlier decompile output is kept in `Tools/re/out/*.c` (untracked). The files used this session: `inst*.c` (instance
connection), `inst11.c` (the whole instance dispatcher, 5275 lines), `inst10.c` (instance factory), `obj1.c` (world
handler), `deser*.c` (component deserializers), `m*.c` (message classes), `gor*.c` (object type register).

## 2. Python helpers (`scripts/re/*.py`, need `pefile` and `capstone`)

| Script | Usage | Notes |
|---|---|---|
| `fn.py` | `<va>… [--callers] [--callees] [--show]` | the function that contains an address (exact bounds from the PE's `.pdata`), its file in `Tools/re/out/decomp-all`, the functions that name it / it names in the decompile, its code. Start here with a trace's caller VA. Index `Tools/re/out/fn-index.json` (local): the first run builds it (~15 min, reading every decompiled file), later lookups take under a second |
| `dis.py` | `<va> [before] [after]` | quick disassembly with RIP-relative string hints. Starting `before` bytes back can land mid-instruction and print nothing; use 0 or a function start |
| `xr.py` | `<string>` | find a string (ASCII/UTF-16) and its code xrefs. Slow on short, common strings; use `strexact.py` + `Callers.java` instead |
| `strexact.py` | `<name>…` | exact NUL-terminated ASCII (`a:`) and UTF-16 (`u:`) occurrences |
| `vt.py` | `<vtable>…` | slots 0–5 of a vtable |
| `scanvt.py` | `<func>` | every vtable whose slot 1 is `<func>` (with `0x1429a7660` it lists the network handler groups) |
| `finddisp.py` | `<lo> <hi> <substr>` | disassemble a range and show the instructions before each operand match, e.g. `"+ 0x980]"`. Found field writers the decompiler hid |
| `statkeys.py` | `<key>…` | client code that reads a stat key (`mov edx, key` + call) |
| `dispatch_ids.py` | `[id…]` | instance dispatcher id → switch case (byte table `0x14215b764`) |

Run with `python -I` (isolated) and add `-X utf8` when the output may contain non-ASCII (`dis.py` string hints).

## 3. GGPK file dump (`scripts/re/ggdump`)

```bash
dotnet run --project scripts/re/ggdump -- "Client\Content.ggpk" "<outDir>" Data/Inventories.datc64 Metadata/Characters/Int/Int.ot
```

It uses `GameServer.Data`'s `GameFileSystem` (bundle index + bundles) and a Node-based Oodle decoder
(`Tools/ooz/ooz_worker.mjs`; the managed decoder lacks Selkie). The GGPK opens read-only and shared, so this works while
the client runs. The bundle index also stores every path (about 1.2 million): `list <regex>` as the first path prints the matching
ones (e.g. `dotnet run --project scripts/re/ggdump -- Client/Content.ggpk out list "beachtown_v2/"`). `.ot` files are UTF-16
with a BOM; `.datc64` files are rows followed by `0xbb×8` and the variable data section.

## 4. Frida through REA (live client)

REA's MCP tools: `start_frida_session` (mode `attach`, `pid`), `load_frida_script` (inline source), `frida_session_status`
(queued `send()` messages), `close_frida_session` (detach; it never kills the target).

Workflow per run:
1. `scripts/dev/live.ps1` (see [../dev/live-testing.md](../dev/live-testing.md)) → client pid
2. `start_frida_session {mode: attach, pid}` **before** logging in or Play
3. `load_frida_script` with `scripts/re/frida/common.js` followed by one of the trace scripts
4. log in, Play, then read `frida_session_status`

| Script | What it does |
|---|---|
| `common.js` | rebases Ghidra VAs (`at(va)`), formats addresses back (`va(p)`), reads the blob reader position (`pos(r)`) and installs `Process.setExceptionHandler` with an image-relative fuzzy backtrace |
| `trace-player-blob.js` | the 0x2bb blob bytes, the created object's component vtables in order (`obj+0x10..+0x18`, each slot +0x88) and the reader position around each component deserializer |
| `dump-walkability.js` | hooks `0x1410e00bd` and writes the area's terrain walkability (`walk-meta.json`, `walk-a.bin` = walkable mask, 5 = walkable; `walk-b.bin`) to `Tools/re/out/`. Used to pick the spawn ([../protocol/03-area-and-world.md](../protocol/03-area-and-world.md) §7) |
| `trace-pong.js` | the 0x1c read, the send-time queue pop and logger calls after the first 0x1c (used to find the HNC violation) |
| `trace-missing-files.js` | paths the file lookup `0x1421d2bf0` fails on (C++ exceptions thrown via `0x1421d2ebe`) |
| `trace-http.js` | every outbound socket send reduced to a plaintext HTTP request line + Host, or the SNI of a TLS ClientHello, plus `connect` targets and `getaddrinfo` names — the shop's web endpoints without decrypting anything. `run.py <name> --follow` waits for a *new* process, so it can catch a client's startup traffic (found `web.poecdn.com` and the empty cosmetics panel) |
| `check-components.js` | any created object (0x2bb): per component the live vtable, its deserializer and the bytes it consumed, the leftover, and the component that was reading on a Buffer Underflow (section 7) |
| `trace-skills.js` | every skill added to an ActiveSkillManager (`SkillInstanceAdded` `0x141ef0f00`): id, action id, effect name, backtrace |
| `trace-skill-stats.js` | stat getter calls (key, value, caller) while a skill action is created or started |
| `trace-move.js` | server and client Positioned moves (`0x141e37740`): cell, rotation, height, duration, flag bytes |
| `trace-actions.js` | S→C 0x1ab on an Actor, the skill-usable check and the action created |
| `trace-action-start.js` | whether an action starts (0x141e937b0) and the actor's current / queued actions |
| `trace-action-life.js` | how long actions stay current, and the attack-time stats' values |
| `trace-queued-check.js` | why the runner 0x141e94900 drops a queued action |
| `dump-stats-object.js` | the stored (key, value) pairs of every stats object asked for one key (set `KEY`) |
| `find-string-refs.js` | standalone: finds a UTF-16 string (`NEEDLE`) in the client's writable memory, then the pointers to each hit. Asynchronous and slow (minutes over ~6 GB); found the tile objects' virtual `.ao` names. Wait for `done` before closing the session: closing waits for running scans, and a synchronous `scanSync` over all memory hung the session and lost the client |
| `trace-cluster-ids.js` | every cluster-jewel node id (`FUN_141f15060`): the slot row, size tag, orbit position and id, plus the node constructor's (`FUN_141f17de0`) template index, start and total. Printed the measured id formula for tree-socket and nested cluster jewels |
| `trace-items.js` | every item the client reads (type hash, bytes), each item component's read and the bytes it consumed, and 0xc0 inventory reads ([../protocol/13-items.md](../protocol/13-items.md)) |
| `trace-flask-use.js` | 0x263 item-use reads, the two native use callbacks, the sound path they pick and its audio submission: what a flask press's confirmation actually plays (13-items.md §8) |
| `trace-flask-mana.js` | read-only flask/mana probe: outgoing 0x24/0x25/0x70, the client's mana pool per change and every 0x263 result — used to find that the client pays for attack-moves the server only walks |
| `trace-enter-area.js` | milestones (SetLocalPlayerId, EnterArea, WorldActivate, CreateObject, UI `0x14078a490`), inventory registrations, passive slot creation and **inventory lookups that return null** (with the row and caller) |

Notes:
- An exception message with `rdi = 0x19930520` and a `RaiseException` frame is a C++ throw. `rbx`/the second arg is the
  throw info (e.g. `0x143a5c5c0` = the blob reader's out-of-range exception).
- `Thread.backtrace(…, FUZZY)` also lists stale return addresses. Read it together with the disassembly.
- Returning `false` from the exception handler lets the client continue normally (dialog or crash).

### Level generation trace

`live.ps1 -Walkmaps` also traces the level generator (every random draw and the placed tile grid) into
`data/walkability/<area>_seed<seed>.gen.json`; `dotnet run --project scripts/re/levelgen -- hash <file>…` checks the
tile hash. Traces also hold `graphs`, the tile graph at the layout's stage boundaries (`hookGraphStages`); `levelgen
generate` compares the port's graph with them stage by stage; `relaxRecords` holds the relaxation's placement
records. See [011-level-generation.md](011-level-generation.md) §8.

## 5. Windows crash data

Without Frida, a crash leaves only the Application event log (event 1000: exception code and fault offset, i.e.
`VA - 0x140000000`). WER kept no dump. Enabling LocalDumps would need a registry change (HKLM), which is the user's
call. There is no `cdb`/WinDbg installed.

## 6. Recipes that worked

- **Message layout:** find the case in the dispatcher (`dispatch_ids.py`), read the factory case for the class ctor,
  `vt.py` the vtable, then decompile slot 1 (write: wire order) and slot 2 (read).
- **Who sets field X:** `FindSenders.java X` (displacement users), then `finddisp.py` over the candidates for
  `mov … [reg + X], …`.
- **Component class from a name:** `strexact.py Name` → `Callers.java` on the ASCII address → the static initializer in
  `0x14006xxxx` (`lea rdx,name; lea rcx,pool`) → `Callers.java pool` → factory → ctor vtable. Then confirm with
  Frida, because live objects can use other vtables.
- **A portal object after the fact:** `python -I scripts/re/portal_capture.py` decodes every `0x02bb` map portal and
  `0x008f` portal list in `logs/live-gateway.log` (to `Tools/re/out/portals.json`): position, place index, portal type,
  open flag, the `0x008f` entry's area, portal variation (+0x26) and owner. The Gateway caps a logged hex dump at 96
  bytes, so a six-portal list and a long `.ao` path arrive partial (the capture marks them `truncated`; an effect's
  `loggedAo` from the log line is the complete path).
- **What a crash needs:** Frida backtrace → decompile the faulting function → find the null field → find its writer
  → find the message that calls that writer.
- **A UI flag the Gateway has never sent:** skip Ghidra. Filter unread instance messages from `schema.md` that contain
  a loop, map them with `dispatch_ids.py`, probe the short ones with `/raw`, then walk `Game.exe` with Capstone
  (`.pdata` bounds, displacement scan, RIP strings). Worked for Atlas completion ([013](013-atlas-progress.md)):
  `AtlasMapCompletedFrame` → drawer `+0x9ce` → set at instance `+0xd070` → S→C 0x1a3 / 0x1a4.

## 7. Schema generators (objects and messages without live trial and error)

| Step | Command | Output |
|---|---|---|
| Component registry | `bash scripts/re/ghidra/g.sh ComponentRegistry.java 'C:\…\Agents\docs\re\components.json'` (40 s) | [components.json](components.json): every `0x140e35f40` registration → pool → users (factory) → every vtable they install, subclasses, slot 17/20, a `primary` flag (factory's own vtable and its subclasses) |
| Registry doc + Frida tables | `python -I scripts/re/gen_components.py` | [components.md](components.md), the `VT`/`DESER` tables in `frida/check-components.js` |
| Component order per type | `python -I scripts/re/ot_components.py --check --md Agents/docs/re/object-types.md` (any `Metadata/…` paths print their list; `--all --json <file>` does every type in the dat tables; `--items --json data/item-types.json` does every BaseItemTypes row from the `.it` files) | [object-types.md](object-types.md). `--check` compares with the two traced objects and exits 1 on a mismatch |
| Read sequences | `bash scripts/re/ghidra/g.sh ReadSchema.java 'C:\…\Tools\re\out\readschema.json' 'C:\…\Agents\docs\re\components.json'` (about 10 min), then `python -I scripts/re/schema_md.py` | [../protocol/schema.md](../protocol/schema.md): every primary deserializer and every message class of the four S→C factories (instance, world, login, patch; each list equals its factory switch's explicit cases), validated against the traced byte counts (exit 1 on a mismatch). The decompiled C stays in `Tools/re/out/readschema.json`; the script publishes only our notation and exits with an error if any C is left |
| C→S messages | `bash scripts/re/ghidra/g.sh SendSites.java 'C:\…\Tools\re\out\sendsites.json'` (15 s), then `python -I scripts/re/c2s_md.py` | [../protocol/c2s.md](../protocol/c2s.md): every client → server message of the instance, login and patch connections with its class, writer, length and senders. Senders outside `0x1421752b0` are a hand table in the script. Exit 1 if an exact length disagrees with the Gateway's `KnownClientLength` |
| Live check | `common.js` + `frida/check-components.js` (section 4) | bytes per component of every object the server sends |

The offline command `python -I scripts/re/c2s_md.py --instance-length-candidates` reads the checked-in
[message inventory](../protocol/c2s.json) using only the Python standard library.
It writes sorted C# switch arms for exact instance lengths to standard output and the fixed-table audit to standard error.
The plaintext opening, dedicated framer IDs and messages without an exact length stay outside the candidate table.
A mismatch or a fixed ID missing from the inventory stops candidate output.

Rules the generators rely on (all from the code, see the script headers): the object-type constructor `0x141ec7c10`
creates Positioned first; `.ot` sections merge parent-first with the first position kept; `type+0x80` components (the
non-client ones) are deserialized; live objects often use a subclass vtable (`ClientPlayer`, `ClientActor`,
`ClientAreaTransition`, `ClientInternalStats`). To build a new object: take its type's list from `ot_components.py`, find
each component's live vtable (★ in components.md, or `check-components.js` on a client-created object), and write the
bytes its summary in schema.md asks for; then confirm with `check-components.js` that every component consumed what
the server meant and `left` is 0.

## 8. Path of Building (formula reference)

For damage/defence rules that are not visible in the client tables, Path of Building is the reference (not a runtime
dependency and never committed): clone `https://github.com/PathOfBuildingCommunity/PathOfBuilding` into the
git-ignored `Tools/PathOfBuilding` (the clone used for this work is commit `16de4b8`). Resistance floor, caps and the
final clamp come from `src/Modules/Data.lua` (`ResistFloor = -200`, `MaxResistCap = 90`), `src/Data/Misc.lua`
(`base_maximum_all_resistances_% = 75`) and `src/Modules/CalcDefence.lua` (`final = max(min(total, max), min)`).
Our `DamageModel` cites those files; keep the clone local and pin the commit when a formula is copied into the server.

**Caps and floors:** always verify a cap/floor in the pinned PoB clone and cite the exact file and commit; never
assume the value from the client panel or from memory. Examples already in `DamageModel`: `ResistFloor = -200`,
`MaxResistCap = 90`, default maximum resistance 75, `SuppressionChanceCap = 100`, `SuppressionEffect = 40`.

## 9. Overnight full decompile

`scripts/re/overnight/overnight.ps1` decompiles the whole client unattended into `Tools/re/out/full/` (git-ignored); start
at `REPORT.md` there. It is optional: the servers, `live.ps1` and the tests never read its output, and nothing it needs
is needed to run or build LocalExile. `-Status` and `-Stop` control it, and rerunning resumes it. It works on its own copy of the Ghidra
project, so `g.sh`, `decomp-all` and `Client/` are untouched, and it never starts the client. After each program's first
decompile it runs the passes of section 10 that auto-analysis needs (fragments, switches, code in no function, then
decompile and dump again, and `NightSplit` for a function still failing); the content track ends with `decode_all.py`.
These passes also need Python with `capstone` and `pefile`, and Unicorn in `Tools/re/py` for the 9 `.ast` blocks.

| Output | Contents |
|---|---|
| `game/decomp/` | same layout as `decomp-all`, with RTTI methods named `Class::vf<slot>` and Parameter ID prototypes |
| `game/*.tsv`, `types.h`, `disasm/`, `index.sqlite` | call graph, data references, strings, symbols, vtables, types, every instruction, all queryable |
| `game/undecompiled.tsv` | executable bytes in no function: what the decompile cannot show |
| `bins/<file>/` | the same for the client's other programs (`Client.exe`, `Game_x64.exe`, `PackCheck.exe`); third-party DLLs are not decompiled |
| `content/files/`, `content/textures/`, `content/extensions.tsv` | every GGPK file, NTFS-compressed only when free space is short (`.dds` 94 GB, 1.5:1 with LZX); the index's size per extension |

Measured on Game.exe: `.pdata` adds only 71 functions to the analysis (it had the rest), and after the fragment and switch
passes 28 KB of `.text` stays undefined, mostly multi-byte nops. `Client/PathOfExile.exe` here is only the first 1.7 MB of `Game.exe`, so its headers
describe code the file lacks; do not analyze it. A decompile's quality is capped by the binary: locals and most functions have no names.

`NightRetry.java` gives failed functions a long timeout and falls back to p-code, which the earlier decompiler passes can
still return (they print no C); such a file says so on its second line. Measured on this laptop:

| Game.exe function | What happened |
|---|---|
| `0x14124b4e0`, `0x14194c880` | One huge function, 16k and 18k instructions: 69 and 88 minutes, about 6 GB of memory each, so they spill into the page file. The first registers about 1,025 skill behaviours by name through `FUN_141947300` |
| `0x14136ba90` | Two adjacent jump tables of 6 entries (at `0x14136bc50` and `0x14136bc68`), indexed by a signed byte: the decompiler reads past them into code and flow runs away. Decompiles with `JumpTable.writeOverride` and the gap `0x14136bbcc`-`0x14136bc0b` added to the body; a rerun needs that by hand |
| `0x140f63d50` | The decompiler process dies; p-code from the `register` pass only |
| `0x141032840` | "Symbol offset is incompatible with VariableGroup", even with the stored prototype dropped; p-code from the `normalize` pass only |

## 10. Game data formats (`scripts/re/formats`)

`python scripts/re/formats/decode_all.py [--no-write] [--ext .ot,.pet] [--fails N]` decodes the files of section 9's
`content/files` into `Tools/re/out/full/decoded`, with a `REPORT.md` of decoded, partial and failed per format. Object files
also get a readable `.geal` of their scripts, and `geal-functions.json` lists the client's built-ins with their script
call counts.

GEAL, the script language of `on_xxx = { ... }` in `.ot`/`.ao`/`.it`, is compiled by the client to bytecode. Compiler:
statements `0x141df8210`, assignment `0x141df8780`, `local` `0x141df91a0`, `if` `0x141df9680`, expressions `0x141dfa050`,
binary operators `0x141dfa8c0` (precedence table `0x142fc8558`), lambdas `0x141dea480`, interpreter `0x141df0d70`. Surprises:

- Built-ins receive their arguments as token ranges (`Range<ScriptToken>`) and read them themselves, so `<root>`, `= 0`,
  `> -1`, `a | b` and `name : value` are legal arguments, not expressions. A comma before `)` is legal too.
- Control flow is mostly built-ins with code blocks (`If(...)`, `IfState(x, = 0, { ... })`); the `if` keyword is rare.
- A `//` comment ends at a lone `\r`, and some files have one: reading to `\n` swallows the next line's event.

Other formats:

- `.mtd` is a stream of tokens, not lines (`0x1429f9240`): group name, mix count, sprinkle count, materials, each with an
  optional `.dlp` after it. One file has `0"Art/...` with no space.
- `.pet` from version 4 holds its GPU emitters as JSON after the old emitter blocks, and one file has a whole second `.pet`
  after that JSON. `.mat` and `.env` allow trailing commas in their JSON.
- `.act` statements span lines and `then` continues the previous action.
- `.gm` is a character snapshot for the Hall of Grandmasters, with no reader in the client. Its layout rests on every file
  ending on its last byte and on the hashes: stats are `Stats.HASH32`, gems `GrantedEffects.HASH32`. The header has the stat,
  item, skill-section and last-list counts. A gem's last byte counts the (stat, value) pairs after it. Item and last-list
  fields have no names.
- `.ui` is a text language too (`template X based on Y`, `begin Type`, `key = value;`), in UTF-16.
- Meshes: `.fmt` `0x1429f2330`, `.tgm` `0x1429eb4c0`, `.smd` `0x1429f0220`, `DOLm` chunk `0x1429f48b0`, vertex layout from the
  flags `0x14105d450`. Before `DOLm` (`.fmt` < 9, `.tgm` < 9, `.smd` < 3) there is one LOD inline. Old `.tgm` store vertices
  before indices and its ground stride depends on a header byte (1: 20, 2: 24, 3: 28, measured). Indices are 32-bit above
  65535 vertices. The bounding box is xmin xmax ymin ymax zmin zmax; some meshes have vertices outside it. The JSON says where
  the geometry is, it does not copy it.
- `.ast`: the clip count is a u32 (the player classes have hundreds of clips), and clips with a second name exist. All clips
  of a file are one Oodle block in the bundle layout, and a clip's offset and size are in the unpacked data. A clip has a
  track per bone: a byte, the bone, six key counts (a u32 more from version 11) and plain float keys. The first three counts
  are scale, rotation and translation (time and values); the other three use the same key sizes and their meaning is unknown.
  ooz-wasm rejects nine small blocks at their real size. Eight unpack with a larger output buffer, and their tracks parse from
  the first `size` bytes; `art/models/cinematic/facecrack/effects/rig.ast` (Leviathan) does not.
- `<file>.header` repeats what the client needs before streaming the file (`0x1411ce9f0`, `0x1411ceef0`, `0x141149220`, u64
  counts, padded with zeros): `.dds.header` holds the DDS header and smallest mips.
- `.tdt` (`0x1429fc500`): a file with a parent has a mask byte saying which groups it overrides. Grids are LZ4 blocks that
  unpack to 1, 2, 4 or 8 bits per cell plus a palette (`0x1429ff000`). Names of its later fields are not known.
- `.mtp` (`0x141178730`) is a texture atlas keyed by `.tdt` path, `.pjd` (`0x141f03cf0`) three record lists with ranges into
  the last, `.slg` (`0x1424e0d00`) 16 raw bytes.
- `.itemblob` is an item saved when its league shipped, so older ones differ from the current reader in
  [13](../protocol/13-items.md): 1-byte item flags, u8 Mods flags, and 0 to 2 bytes after Mods and SkillGem. The layout is
  chosen by using every byte. An item component's reader is slot 5 of its instance vtable, which ends right before the
  component's descriptor vtable (the one its registrar stores next to the name), so that table gives the reader.
- `cachedhlslshaders/<api>/<hash>`: first byte 0x0f DX11, 0x0a DX12, 0x12 Vulkan, then reflection, u32 size, the DXBC or SPIR-V,
  a trailing u32 hash (Vulkan: 5 bytes before it). The first three u32 look like the compute thread group. Vulkan names its
  entry, inputs, outputs, constant buffers with fields, and resources; DX has only hashes.
- `data/mat_table.dat` (`0x1410f8190`, version 2) and `data/languages.dat` are not schema tables. The 100 `.datc64`/`.datcl64`
  files with no table in `data/schema` have no rows.
- A few `.itemblob` are older than that: mods no longer in the tables, known mods with a value more or fewer than the tables
  give now (`table_values`; uber map mods gained a last stat after Necropolis, `LoseSoulEater` keeps a second range with no
  stat key), bytes in components that read none now, no byte for Mods bit 0x10, a Map without the u32 or with 2 bytes. Value
  counts come from what parses the whole file; misaligned readings still hit real mod keys, so readings are ranked by mod domain
  and values within the table's ranges (`readings` > 1: not unique). The save is the only evidence: no client compares them.
- ooz-wasm rejects 9 `.ast` blocks. `scripts/re/formats/oodle_client.py` runs the client's own `OodleLZ_Decompress`
  (`0x142c9d520`) in Unicorn (`pip install --target Tools/re/py unicorn`), so it also settles what a block really unpacks to.
- `.tgm` record: u16 index, value, bbox (min, max per axis; up to version 8 bbox first, then first triangle and count). Light
  (`0x1429eb4c0`): kind, colour, position, direction, radius; size by version. `.smd` sections are collision ellipsoids and
  spheres with a bone, by their names. Other field names are neutral.
- At-exit stubs and similar code nothing calls directly are outside auto-analysis and `.pdata`: `NightFragments.java` makes
  functions at entries from a linear disassembly of `undecompiled.tsv`, so a few may be misread data. Its `instructions` mode
  covers code Ghidra already disassembled. `NightDump` skips tables that exist: delete them to include new functions.
- MSVC switches with `__assume` have no bounds check, so auto-analysis misses them: `LEA RCX,[image base]`, `MOV EAX,[RCX + RDX*4
  + rva]`, `ADD RAX,RCX`, `JMP RAX`, with the table inline after the function. `NightSwitches.java` recovers them with a jump
  table override and clears tables misread as code; functions in its `switches.tsv` need decompiling again. In some
  functions with byte index tables the override kills the decompiler process though the table is right (seen in 11 functions of
  a third-party DLL), so `NightRetry` drops it and the file says so.
- A function too big for every decompiler pass (seen once, in a third-party DLL: 413 KB, 24,649 blocks) is written by
  `NightSplit.java` as pieces of C; instructions in no piece that decompiles stay disassembly.

## 11. Porting to a new client build (`scripts/re/port`)

How 3.29.3b → 3.29.3c was ported; every step maps from evidence, nothing by offset guessing.

1. **Address map.** Import and analyze the new executable into a separate Ghidra project, add the old one, and run
   `AutoVersionTrackingScript.java` with `PortVtOptions.java` as the first post-script (the stock options without the
   duplicate-function correlator, which ran for hours without finishing). Run `NightRecover.java` and
   `NightSwitches.java` on both programs (auto-analysis left the instance dispatcher's jump table unrecovered in
   3.29.3c only, so the two bodies did not align). Then `PortMap.java <session> <out dir> <addresses.txt>`: it aligns
   every accepted function pair instruction by instruction, pairs the functions Version Tracking left (unanimous call
   targets, vtable slots, and bodies found once in each program, each checked by alignment), and resolves each listed
   address with how it was proven. 3.29.3b → 3.29.3c: 75,880 accepted + 26,282 propagated functions; 9,819 of 10,321
   repository addresses. A vtable slot is weaker evidence than a call target: 3.29.3c swapped two near-identical
   slots of one class (3.29.3b 0x141dcbae0 and 0x141dceda0), and only the callers and the bodies told them apart.
2. **Message ids.** Run `ComponentRegistry.java`, `ReadSchema.java` and `SendSites.java` (addresses rewritten through
   the map) on the new program, then `compare_messages.py`: S→C factory cases pair by mapped class, C→S sends by
   mapped function and class. Classless ids routed by a handler's own byte table (the world group's Positioned
   `0x2b2`) are checked in that table.
3. **Tables.** `compare_tables.py` on both decoded table folders: row moves by key and reordered columns. A saved
   row index needs a migration (`mtx_remap.py` made `CosmeticMigration`).
4. **The rest by hand.** Code outside any function or in an unmatched one: a masked byte signature found once in each
   build is only a candidate (a 4-instruction match landed on another string's `lea`); it needs a second proof, such as
   both mapped neighbours' shift, the same string or pool reference, or every mapped data reference. Data: equal bytes,
   or words whose pointers map, found once. Add the proven pairs to `resolved.tsv`; what has no proof stays unported
   and is labelled with the old build.
5. **Rewrite.** `rewrite_addresses.py <resolved.tsv> <report.tsv> [--apply]` replaces mapped address tokens in tracked
   files and lists the rest; generated docs (section 7) are regenerated on the new build instead. A token glued to a
   name (`byte_table_0x…`) is not matched. `rewrite_message_ids.py <report.tsv> --shift first:last:delta …` moves
   message ids; its report needs reading line by line, since stat keys, offsets after another id (`0x22 + 0x2b9`) and
   ClientStrings rows share the range. Re-check every script's code signature against the new executable.

**The 3.29.3.5 hotfix needs no port.** Its `PathOfExile.exe` (build 328126, still "3.29.3c") has the 3.29.3c release's
sections and all 148,967 `.pdata` functions at the same addresses; every `.text` difference is a RIP-relative
displacement, because `.rdata` and `.data` moved by a few bytes (mostly -0x30, +0x10, -0x80). Code addresses and
message ids are unchanged; addresses of globals, vtables and strings are not, so a script that uses one needs the
hotfix value. The login-key offset (`0x2fc91f0`) and the HNC mask are equal. The patch changed only
`Bundles2/Tiny_02.bundle.bin` and the index: no table, and one server-read file
(`metadata/pet/edictspearutilitypet/edictspearutilitypet.ot`). Check a future hotfix the same way: if the `.pdata`
tables are equal, only data addresses need the new values.
