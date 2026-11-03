Loot timing capture for the frozen x64 3.29.3b client

Run commands from the server repository root. Python 3.11+ and Frida are needed
for capture; report.py uses only the standard library. The local .venv has both.
This tool attaches to an already running explicit PID. It never launches a game,
updates archives, sends packets, clicks, or writes gameplay state. Frida installs
instrumentation hooks and therefore has overhead. Use an authorized reference
setup where this instrumentation is permitted; it is not certified for official
service policies or timing neutrality. Local clients start only through live.ps1.

QUICK START (PowerShell)

  . .\scripts\dev\local-env.ps1

1. Check the reference executable without attaching:

  python scripts/re/loot-timing/capture.py --check Client/Game.exe

The two accepted SHA256 digests are the stock and locally patched frozen binaries
listed in capture.py. A renamed identical stock binary is accepted; a different
binary is refused. Runtime PE timestamp/image size and every hook signature are
also checked before any hooks are installed. Do not edit hashes to force another
build through: addresses and layouts must be re-established first.

2. Find the running reference client PID using Task Manager, or:

  Get-Process Game,Game.patched,PathOfExile -ErrorAction SilentlyContinue | Select-Object Id,ProcessName

3. Attach, replacing 12345 with that PID. Choose a NEW output filename each time:

  python scripts/re/loot-timing/capture.py --pid 12345 --reference official --out Tools/re/out/loot-official-01.jsonl

For LocalExile use --reference local. Attach before entering the test area when
possible, then wait for loading and ground items to finish before arming a trial.
The tool prints Recording only after the observer has started successfully.

4. At the console enter:

  k          arm an isolated monster-kill trial
  c          arm an isolated chest-opening trial
  p          arm one pack encounter (individual item ownership stays unknown)
  e          end the current trial after all loot has arrived
  m note     add a note, e.g. m Coast rare cold-no-shatter
  q          finish the capture and detach

Optionally specify a known object ID: k 0x1234 or c 0x5678. IDs are validated by
the report. Never operate a second monster/chest within one trial. Arm BEFORE
the action; wait at least five seconds after the final drop before ending, longer
for delayed bosses. Keep items on the ground, remain nearby, and avoid changing
areas during a trial. End the trial with e before q. Interrupted/open trials are
invalid, even when some events were recorded. Zero-loot trials are retained.
Typing the commands is only a trial boundary: it is NOT the measured start time.
For p, kill one pack without opening chests, then stop and wait for its loot.
Pack reports allow multiple monster deaths and spawned pack members. delay_ms is
relative to the first observed death at each stage; delay_from_last_source_ms is
relative to the last. Negative last-death offsets mean items appeared while combat
continued. These are encounter intervals, not individual kill-to-drop delays.

5. Analyze offline:

  python scripts/re/loot-timing/report.py Tools/re/out/loot-official-01.jsonl --out Tools/re/out/loot-official-01-report.json

The console gives per-trial delays/spacing and kill/chest summaries. The JSON
retains every item ID and per-stage timing. Invalid trials return a nonzero exit
status; their diagnostic metrics must not be used as accepted measurements.
Never concatenate captures. Original JSONL and report files are never overwritten.

WHAT THE NUMBERS MEAN

decoded_intake:
  Time from the source's decoded death/open message entering the World intake to
  each item's decoded create message entering it. This is after deserialization,
  before the native World's scheduling queue. It is NOT socket arrival/send time.
dispatch:
  Source-message dispatch entry to each item's create-message dispatch entry.
construct_from_source_apply:
  Just before life becomes zero / Chest open byte changes, to the WorldItem
  component reader during construction. This does not prove rendering/landing.

Each stage reports delay_ms for every item, spacing_ms between consecutive items,
first_ms, and total burst_ms. The source is a positive-life-to-zero authoritative
pool update (0x1b1 pool 0), or Chest's closed-to-open transition from 0x1bd. The
probe does not assume a click is a kill or a successful open. Duplicate zero/open
updates do not create additional source transitions.

Ground items are identified by the actual WorldItem component deserializer, not
by a hash-name guess. Construct scopes associate them with the create message.
Objects' metadata paths are best-effort bounded reads; a kill without positive
monster identification is rejected. Items have no captured explicit source link:
one-source isolated trials are attribution candidates, not proof of ownership.
For single-source trials, multiple deaths/opens or monster loading invalidate the
trial. Pack trials permit monster construction/deaths but reject chest openings.
Unmatched message IDs, source/item
intake before arming, read errors, missing records, overflow, or incomplete native
scopes invalidate a trial/capture. No automatic nearest-corpse assignment is made.

QPC timestamps preserve native clock ticks as decimal strings, avoiding JavaScript
integer precision loss. The report subtracts integer ticks before converting to
milliseconds. Clock resolution is not measurement accuracy: hooks, deserialization,
frame pumping and network batching all affect observed intervals. Compare idle
video-only and instrumented runs for gross observer effects before drawing parity
conclusions. No exact server timer can be recovered from client arrival alone.
Render/landing/label/pickup timing remains outside this probe; record video if needed.

The observer reads only whitelisted message fields and object metadata, without
authentication, raw packet buffers, item contents, or game function calls. Capture
manifests contain the executable path and PID; review them before sharing. Output
under Tools/re/out is ignored by Git. No executable or game archive is redistributed.

NATIVE PROFILE EVIDENCE (stock image base 0x140000000)

World intake 0x1421872b0: R8 points to owned parsed-message pointer.
World dispatch 0x142187480: RDX is parsed message; kind +8, object ID +0x10.
Create constructor 0x141ebe190: RDX points to type record, R8D is object ID.
WorldItem deserializer 0x141ea10a0: observed inside create scope.
Pool apply 0x141e69fb0: RCX is pool; pool+8 -> Life; Life+8 -> entity.
Entity+8 holds the shared type record (0x142427360); entity+0x78 is the World,
not the type record. Source metadata reads use entity+8.
Chest write 0x14218823f: before mov [r14+0x168],dl, in dispatch 0x1bd;
  R14 is Chest, DL is new open state. Old +0x168 distinguishes the transition.
These locations/layouts were checked against the pinned binary's disassembly.
Existing protocol docs: 02-instance-connection.md, 04-player-entity.md,
schema.md (0x1b1 / 0x1bd), and re/projectiles/world-message-inputs.md.

VALIDATION STATUS

The pinned stock executable passes digest validation; JavaScript/Python syntax
checks pass. A five-second observer-only smoke capture on a named local client
attached and detached with zero errors, drops, or pending scopes. This is hook
installation validation. Official runtime observations now confirm monster life-zero,
Chest closed-to-open, and WorldItem construction events. An ordinary zero-loot
chest control, an isolated quest-chest drop, and a combat encounter were accepted.
Mixed-activity single-source trials remain excluded; render timing is unmeasured.
These observations do not yet establish a universal official loot timing rule.
A separate official installation matches the pinned stock digest;
an official-client attach/detach check also completed with zero errors/drops/scopes.
