# 07 — Latency (0x1b/0x1c and HNC), visibility (wake zone) and movement

State of 2026-10-16: the client's "network" graph stays flat (0–14 ms on localhost since 2026-10-19, §1), the Witch is drawn in Lioneye's
Watch, and clicking the ground walks her there at normal speed. Evidence:
[../re/evidence/2026-10-16-witch-visible.jpg](../re/evidence/2026-10-16-witch-visible.jpg).

## 1. Tick and ack: C→S 0x1b, S→C 0x1c

The client sends **0x1b** every 100–500 ms in state 3 (u8 counter + 4 bytes, 7 bytes in all; `0x142148880`) and pushes
the send time onto a queue at instance `+0xc458`. Each frame it sets `latency = now − queue.front` (or 0 when empty);
that is the perf overlay's "network: N ms". Without an answer the queue only grows, so the value climbs forever.

**0x1c** (instance group, dispatcher case 5, class `0x1435dc8c0`, read `0x141fa0720`): `u16 server delay ms`,
`u16 index`, `u32 HNC carrier`, all big-endian. `0x142155930` drops queue entries 0..index and returns entry `index`'s
send time; an index ≥ the queue's size clears the queue and returns `+0xc470`, the latest tick's send time (live Frida
2026-10-19). `+0xc488` is not a round trip: every client send stamps it with the time. One 0x1c with index 0 per
received 0x1b keeps the queue at the one tick in flight (`InstanceProtocol.Pong`).

Ticks that no server answers stay in the queue for good: those sent while the client leaves an area, or lost in
unframed bytes. Index 0 then always pops a stale entry. Live in The Coast the queue held 3 at every 0x1c (ticks every
167 ms), and the meter read ~400 ms. The first 0x1c of each connection, and the first after an unframed read, therefore
sends index 0xFFFF (`ClientInbox.NextAckIndex`). After that the queue held 1 at every 0x1c.

**What the latency graph shows.** The network update `0x142148880` runs once per client frame on the network thread:
it reads the socket, handles each 0x1c, sets the sample `+0xc480` = now − queue front (0 when empty), and sends a 0x1b
every 100 ms (500 ms after more than 100 ms without a client send, `+0xc488`). The graph and "latency N ms" show
`+0xc490` (`0x140d57cd0` feeds it to the perf ring). Two places move it:

- the network update raises it to the sample when the sample exceeds it by a margin (a tick overdue);
- the 0x1c handler (`0x14214b8dc`, after the pop) computes rtt = now − the popped send time, subtracts the 0x1c's
  u16 server delay from rtt and from the sample, and clamps the value into [sample − delay, rtt − delay]: it only
  drops when an rtt comes in below it.

So the value is the lowest round trip measured since the last overdue tick, and it cannot go below one client
update: the client reads the 0x1c at the start of the update after the one that sent the 0x1b (live: socket read →
pop → send the next tick, all in one update). Live Frida 2026-10-19 in town: sample 0, value 12, rtt 166–169 ms with
the client in the background (~6 updates/s), so the value stayed at the 12 measured while it ran at full frame rate.
Measured on one QPC clock (Frida on the client and the Gateway, 2026-10-19), the Gateway answers each 0x1b 0.15–0.28 ms
after the client sends it; the rest is the 0x1c waiting in the client's socket until its next update.

Each 0x1c goes out once the input read before its 0x1b has been handled (`ClientInbox.DeferTick`), in tick order, with
server delay 0. The value then reads the whole wait the player feels: the network, the client's next update and the
server's input queue (on localhost a steady ~14 ms, one client update; 100-400 ms in a fight the server lags in).
Until 2026-11-05 it sent 0xFFFF (issue #49), which zeroed both clamp bounds, so F1 NETWORK read 0 always and only an
overdue tick showed.

The 0x1c takes only the connection's wire gate (`Program.Write`: encryption and the socket write), never the send gate
whose packets evaluate game state in their `still`/`atGate` callbacks. Those callbacks wait for the combat lock, and in
a crowd of 148 monsters they held the send gate for up to 4 s, so the answers queued behind them and the meter jumped
to 0.5–4 s. Measured afterwards: no 0x1c later than 50 ms in a crowded Coast session.

Tick latency includes the input queue (above) but not the sends queued at the send gate.
Set `$env:Game_TRACE = 'lag'` for the lag probe (`ConnectionPerformance.LagProbe`, off by default for its own cost):
`[latency-totals]` (cumulative since the connection opened), `[latency-sends]` by message id, `[latency-empty]` by
calling method, `[slow-use]` and `[slow-send]`.
`ConnectionPerformance` separately records socket-read-to-handler queue age, handler duration, send-gate wait/hold,
wire-gate wait/hold, socket writes, hot combat-lock wait/hold, channel lateness, surround time, blocker-index builds and
each client message's handling (`Message`, the message id as the operation, such as `0x0025`).
The `LocalExile.Gateway` meter exports duration histograms in milliseconds, surround allocations in bytes and pending
input batches; tags contain operation names, never character or account identifiers.
The duration histogram also carries `LevelGen`, one sample per generated area with its area id, outside any connection.
An outside tool reads the meter without changing the server, for example
`dotnet-counters monitor -p <Gateway pid> --counters LocalExile.Gateway` or the launcher's Performance tab.
Closed connections discard their abandoned backlog so its depth does not remain in the process total.
At most one `[latency]` log summary is emitted per connection every ten seconds when an interval sample reaches 50 ms.
Set `$env:Game_TRACE = 'latency'` (or the older `$env:Game_LATENCY_METRICS = '1'`) in the PowerShell session before
starting `scripts/dev/live.ps1` to log every interval instead ([debugging](../dev/debugging.md)); read
`logs/live-gateway.log` for `pending-batches` and each phase's `interval-max-ms`.
The logging flag does not change gameplay or the client's latency display.

The full player stat and Actor refreshes (aura, charge, Fortification and Rage changes) build the whole skill list
(`PlayerSkills`) inside the send gate, so every echo and monster packet waits for it. In 2026-10 one build of a 10-gem
level 100 Witch cost 180–270 ms in the Debug Gateway (45–65 ms Release), and an aura toggle ran two or three of them:
move handlers logged 400–730 ms in an empty hideout. Each gem resolved its links and supports (`SupportedSkill`) and the
final player stats five or six times, and every gem requirement check scanned the whole GrantedEffectStatSetsPerLevel
requirement list. One build now resolves each once (`SkillBuildScope`) and the requirements are indexed by stat set:
5–10 ms per build in Debug and Release alike.

## 2. HNC, the hidden side channel

The 0x1c u32 is not free data. `0x141fa0720` feeds its bits 9..31 then 0..8 (LSB first) into a byte accumulator on the
stream (`+0xd0`). Every completed byte is decrypted and run through `0x14299de50`:

- idle: a byte must decrypt to 0; otherwise it must be a packet type `0xDA`–`0xDD`
- then a length byte (≠ 0), then that many payload bytes, queued as a packet
- anything else throws **"HNC Protocol Violated"** (an exception dialog; a 0x1c with u32 = 0 did exactly that)

The HNC cipher is the login-style Salsa20 pair keyed with **km'** = the 64-byte 0x14 key XOR the table at
`0x142fe0da0`, then byte swaps 0↔0x15, 4↔2, 0xd↔0x2a, 0x30↔1, 0x1d↔0x1e, 0x32↔0x13 (`0x14299db60`); the client receives
with IV `km'[48:56]`. So an idle carrier is the keystream itself: `v = rotl(LE32(keystream[4]), 9)`
(`HncSender.NextIdleU32`, tested by replaying the client's unpacking in `HncTests`). The client also sends HNC packets
(type `0xDD` from the tick); the server ignores them for now.

## 3. Visibility: the wake zone (S→C 0x1a9)

An object created with 0x2b9 exists (position, components, camera follow, teleports) but is **not drawn and does not
update** until it is in the wake zone. **0x1a9** (world group, class `0x1435dd838`, object header only: `u16 id, u32
object id, u32 0, u16 0`) sets object `+0x8d |= 2` and calls `0x141ec0aa0` ("ObjectInWakeZone"); 0x1aa removes it. The
Gateway sends 0x1a9 for the player right after 0x21. That alone made the model appear (in the washed-up pose) and let
actions run.

Also sent, not proven necessary: Positioned `flags1 0x02` with (hidden 0, shown 1) (`0x141e2f0b0` → vtable +0xb0), and
Animated bit0 with the path `Metadata/Characters/Int/Int.ao` (the type already supplies it; the Animated deserializer
only reloads when the path differs or none is set). Neither made the model appear before 0x1a9 was added.

## 4. World-group object messages

The world handler `0x142187480` looks up `u32 object id` (message `+0x10`) and routes by id:

| Ids | Goes to | Notes |
|---|---|---|
| 0x51, 0x1ab–0x1b0, 0x1b4–0x1b6, 0x1ce–0x1d2, 0x1f0/0x1f1, 0x261, 0x2c6 | Actor `0x1421940a0` | actions |
| 0x1b7–0x1c0, 0x2b0 | Positioned `0x141e30c90` | position / movement |
| 0x1a9 / 0x1aa | wake zone in / out | |
| 0x2ba | component update by component index | |
| 0x2bb | remove object | |

Object header (all these messages): `u16 id, u32 object id (+0x10), u32 (+0x14), u16 (+0x18)`, written by `0x141f91f60`.

## 5. Movement

**C→S 0x25** "use skill at location" (sender `0x142166300`, class `0x1435bfbd8`, 16 bytes): `u32 x, u32 y` (target grid
cell), `u16 skill id` (Move = 0x2909 for the Witch), `u16` action `+0x11a` (an action sequence number: 0x1001, 0x1002, …;
only sent when ≥ 0), `u16` action `+0x122` (flags, 0x0408). 0x24 is the same aimed at an object id. The client starts the
action itself (prediction) but will not send another until the server echoes it.

**S→C 0x1ab** "action started" (class `0x1435c2340`, read `0x141fdda70`): object header, `u16 flags` (+0x38 → action
+0x122; bit 0x80 adds a `u32 target object id`), compact ints **actor position** (+0x2c/+0x30 → action +0x158) and
**target** (+0x24/+0x28 → action +0x130), `u16 skill id`, `u16 sequence` (+0x36 → action +0x11a). For the local,
predicted player (sequence ≥ 0) the handler compares the position pair with where the client thinks the player is and
**teleports** the player there when they are ≥ 92 grid cells apart (`|Δ|² ≥ 0x2110`); otherwise the echo is just a
confirmation (`InstanceProtocol.ActionStarted`), so any smaller error stays until a walk ends on its target. Live
2026-10-31: an echo 13 cells off moved nothing. Clicks do not wait for the echo: the client sends them as they come.
0x1ac ends an action, but is ignored for predicted local actions; it still counts for the resync request below.

The first version sent the target in the position pair: long clicks teleported the player, sometimes onto unwalkable
cells past the town edge (toward The Coast), where the client could no longer path anywhere. The server therefore keeps
its own copy of the player's walk, and since 2026-10-21 that copy is the client's own algorithm, ported 1:1
(`ClientPathfinder`, `PlayerWalk`). The earlier fitted A* (`WalkEstimator`, now only for pets) drifted by a median
13 cells and up to 50 on held-mouse walks: it walked zig-zag cell paths where the client walks straight lines, rounded
to whole cells on every re-order, walked through NPCs and refused detours the client takes.

**The client's pathfinder** (`0x141e4d890`, from the Pathfinding component's MoveTo `0x141e7b530` → `0x141e7b920` →
`0x141e4ef90`). Arguments seen (Frida, `scripts/re/frida/trace-movement.js`): a Move click or a monster's move has
precision 1, min and max range 0, tolerance −1, no phasing; a walk up to an object to use it has max range = the
object's size + 3 and tolerance = its size (an NPC, size 4: 7 and 4; the stash, 2: 5 and 2; a waypoint, 5: 8 and 5).

**One search budget per frame.** The search's last argument is what the frame has left for it: 1024 at the frame's
first search, what that one left for the next (the player's held-walk search leaves 768), 0 once spent, and a search
given 0 finds nothing (375 of 400, live 2026-11-03). Every object that paths in the frame draws on it, in the order the
frame reaches them, so a server step's chase orders, which arrive in one frame, leave the later monsters standing until
their next order: with eight Rhoa Mares chasing a predictive client's walking player and every chase ordered in the same
step, 46 % of the monsters' searches found no path (the frame's first 30 %, its fourth 57 %, its eighth always), and the
client's monsters fell 15–20 % behind the server's estimate through the pack until its 46-cell snap put them back.
A monster whose search found no path stays in its cell until its next order (98 %); one with a path moves on (97 %).
Spreading a step's chase orders over the following steps (three or two a step) or ordering Move chases every 200 ms
instead of 100 changed neither the failure rate (42–50 %) nor the lag: a dozen chasers on the 200 ms cadence ask for
more than one search a frame, the client paths again by itself whenever a path the budget cut short ends, and fewer
orders make each stand longer. Given the whole budget, a search still fails by what stands in the way: 6 % within 12
cells, 42–57 % at 13–60 cells (the pack's own bodies, at the client's positions), 27 % beyond 60 (a partial path
counts as found). The server cannot see which searches fail, so it keeps them short instead: a melee chase farther than
a leg past its reach goes as a Move to the cell 12 along the walk-up, ordered again on the chase cadence, and the attack
aimed at the player only from within a leg of the reach; a Move to a surround slot is cut to its first 12 cells the same
way ([11](11-skills.md) §6). With that, 8 % of the client's searches
found no path (6 % given 0), and the pack ran p90 4–17 cells from the estimate instead of 19–28, with 0–2 snaps a run
instead of 1–7.

- **Grid and size.** Walk values are 4 bits per cell in 3 layers (`0x141d85260`: map `+0xd0 + 0x18 × layer`, row stride
  `+0x100`, size `+0x18/+0x20` in 23-cell tiles). A mover of size *s* (Positioned `+0x1e3` = `object_size − 1`: the
  player 1, NPCs 4, the stash 2, monsters 1–3) may stand on a cell whose value is ≥ *s* + 1, so a monster of size 2
  needs ≥ 3 where the player needs ≥ 2. The player uses layer 0
  (Positioned `+0x2c0`), which is byte for byte `data/walkability/<area>_seed<n>.bin`, so walkable = value ≥ 2. Layer 1
  is the projectile grid (`<area>_seed<n>.projectile.bin`: 0 stops projectiles, 1 … 5 a distance from what does): the
  goal's line test reads it (below).
- **Search.** A* in a 272 × 272-cell window (`0x110`, centred on the start). 8 neighbours in the order (0,1), (1,0),
  (0,−1), (−1,0), (1,1), (1,−1), (−1,−1), (−1,1) (`DAT_142fbf350`); no corner rule. Costs are 1 / 1.4142 and the
  heuristic is `|cell − target| + 0.001 × |cross(target − start, cell − target)|` (`0x141e4d7d0`; the distance less the
  tolerance, then the part outside [min, max], 0 inside), all in float32. The open list is MSVC `push_heap`/`pop_heap` on
  `{node, f}` with `a.f > b.f` (`0x141beb420`, `0x141beb2b0`), stale entries are skipped, and the goal is the first
  popped node with h = 0 whose Bresenham line to the target, both ends included, holds the order's precision on layer 1
  (`0x141d87160`; the precision is MoveTo's `+0x528`, 1 … 5: 1 for a Move or a melee attack, 3 for a bow's attack). A
  node in range without that line is expanded like any other, so a bow's attack walks to the nearest cell it can
  shoot from. When no node had it and the best node was the start, a monster's second search goes to the target itself
  (min and max range 0).
- **The player's limit is an ellipse.** For a mover with a Player component (`0x14013f1e0`) there is no expansion
  count; a neighbour n is searched only if `clamp(2 |target − start|, 20, 136) > |n − target| + |n − start|`.
- **Other movers stop after 100 expansions** (1000 for a unique, ObjectMagicProperties `+0x144` = 3, or a pet with
  PetAi). When that search does not reach the target and the straight line to it is blocked, a **coarse pass** runs:
  A* over cells of 11.5 grid units (`(c × 23) / 2`), seeded from the start's coarse cell and its (0,1), (1,0), (1,1)
  neighbours where a straight line reaches them, costs 11.5 / 16.2634, only through cells within 136 of the target and
  a straight line apart, at most 300 expansions; the first coarse cell with h < 23 that sees the target ends it. Its
  chain back (goal first, with the path length from each to the target) then guides a second fine search: a cell heads
  for the next coarse waypoint once it is within √143 and in sight (`0x141e4ee90`), and its heuristic becomes the
  distance to that waypoint plus the length left (`DAT_1445bb850`, `0x1445bb868`).
- **Objects** (`0x141e61370` → `0x141e61540`). Objects are found in the 23-cell tiles within *s* + 4 of the cell; one
  blocks when `max(|dx|, |dy|) − its size < s + 1`, a square (an NPC: Chebyshev distance ≤ 5 from its cell). Objects
  with `blocking = false` (waypoints, area transitions) are never tested. Layer, team and phase flags can exempt an
  object; a walker that starts inside a footprint may cross object cells at cost 21.
- **Unreachable targets.** The search ends at the explored cell with the lowest heuristic.
- **Waypoints.** Walking back from the end, a cell is kept where the Bresenham line (`0x141d87160`, terrain; `0x141e616a0`,
  objects) from its parent to the last kept waypoint is blocked. At most 136 waypoints.

**The client's walk** (Positioned `0x141e34400` → `0x141e4d180`, ticked by `0x141e32710` → `0x141e4d390`). Each segment
between waypoints is a Bresenham line of cells; one step takes `(√(dx² + dy²) / speed) / max(|dx|, |dy|)` seconds,
and the grid cell (`+0x294`) only changes a step at a time. Each frame adds its time to the accumulator (`+0x244`: a
predictive client's own frame, a whole number of milliseconds of its clock; a lockstep client's server frame, below)
and steps while it holds a whole step time (`+0x240`); before every
step the next cell is checked again (terrain, objects) and a blocked step ends the walk: the walker stands, with no new
path and no message (`0x141e7bc20`). A new segment, including the first one of a new order, rescales the accumulator to
its own step time (`A × s_new / s_old`), so a re-order keeps the
elapsed fraction of the step in progress. A stop (`0x141e350e0`) takes effect in the next tick, after that tick's
steps, and keeps the accumulator: the tick clears only the step time, so the next walk, with no step time to rescale
from, starts with it as it is and steps that much sooner.
Verified on a capture (2026-10-21): the per-tick rule held in 55,915 of 56,069 player
ticks (the rest are counter resets), the rescale on 575 of 575 clicks. Speed (`0x141e7c0b0`) is the Pathfinding base
(`+0x548`, `base_speed` 37 for the player, times the Actor's run-speed roll `+0x1a8`) × (1 + `movement_velocity_+%`
/ 100) × (1 + stat 0xce6 / 100), capped by stat 0x44f.

**The lockstep tick** (live 2026-11-03, `scripts/re/frida/trace-lockstep-tick.js` on a bow pack fight). On a
lockstep client the walk above runs per server frame. The world update (`0x142183930`) runs each queued frame through
`0x142183ce0` with the frame's length as float seconds (33 ms → 0.033f); that walks the object tree (`+0xa28`, keyed by
object id) in ascending id and, per object (`0x142184680`; ids with the top bits `0xc0000000` afterwards, through
`0x142184870`), applies the records keyed below the object's id and calls each component's slot `+0x40` with the delta.
Positioned's slot is the walk tick itself (vtable `0x1435a0f18`, `0x141e32710`). So the player (0x1000) ticks before
every monster, a monster before those created after it, and a walker takes all of its steps due in the frame before the
next object ticks (1 488 frames with walker ticks, every one in ascending id). The accumulator gains exactly the frame's
length (18 574 of 18 917 ticks; the rest ended a walk or changed segment), keeps its remainder across steps and
segments (a segment's setup, `0x141e4d180`, rescales it by new/old only when the old step time is above zero: all
1 627 mid-walk re-orders rescaled, all 3 231 segment transitions carried) and is zeroed when the step loop ends the
walk (its path's end, a blocked step), not by a stop: in per-frame traces of six fights (2026-11-04), 28 of 28 player
walks from a stand an action's stop left mid-step started with it unscaled, and 43 of 43 after a path's end or a
blocked step started at 0. A walk set up in the stop's own frame is not traced: by the decompile the client's MoveTo
then clears the deferred stop (`0x141e34400`) and rescales the step time still set (`0x141e4d180`). The estimate gives
such a walk no kept time, and Shield Charge (`0x141a6bae0` → `0x141e9a3c0`, its first step from a stand not traced)
none at all. Each step's
body check (`0x141e47ca0`) reads the other objects' cells as they are at that moment, so an object that ticked earlier
in the frame is seen where that frame's steps put it and a later one where the frame before left it. The forced
position set of an action start (`0x141e8e7e0` → `0x141e31800`: in lockstep whenever the actor's cell is not the 0x1ab's
pair, predictive at 46+ cells) ends the walk in progress and zeroes the accumulator (713 sets in the fight, every one
with the cell differing, 601 on a walking actor, all 601 zeroed; on a standing walker it keeps it, 9 of 9 on
2026-11-04): an order whose from cell is the client's cell keeps
the fraction of the step in progress, one whose from cell differs by any distance loses it, and the actor's next step
comes a whole step after the frame. `PlayerWalk`'s time model (step k of an order applied at frame start S with
fraction f at S − f × step + k × step, from a stand a stop left at S − kept + k × step, with every step already
due placed inside S's frame, before the first one not yet due) puts every step in the frame the accumulator gives it,
up to float32 rounding;
what the Gateway's step pass adds for a lockstep client is the order within a frame and the cells each check sees
(`StepWalkers` → `StepFrames`: whole frames, each walker's steps of the frame in ascending object id against the cells
as moved so far, up to the start of the frame being sent).

**Click timing.** The client applies a click right after its frame's tick and sends the 0x25 in the same frame: on one
QPC clock (Frida in the client, the Gateway's `Game_MOVE_TRACE=1` log), the 0x25 reaches the Gateway's socket 0.24 ms
(sd 0.05) after the client's sender ran, and the tick was 0.74 ms before that read (526 clicks). So the server applies a
click at its read time (`ClientInbox.Batch.ReadAt` → `ClientState.ClockAt`), not when the handler runs. The client sends
no clock: neither 0x25, the 0x1b tick nor the HNC 0xDD (an inventory checksum) carries one. Its game clock counts whole
milliseconds and runs ~1 ms (sd 0.5) before its tick, so a click whose step boundary falls within ~1 ms of the frame can
still differ by one cell.

**Monsters on the client.** A monster's 0x1ab is applied when the client next reads packets, 5–50 ms after the
server sent it (median 17 ms), and the client paths it at once (1,235 of 1,235 recorded monster paths match
`ClientPathfinder` with `SearchLimit.Expansions100`, all other objects as blockers and the size's walk threshold). Each
step is checked like the player's. A path that began on a blocked cell steps through bodies until its first free step
(`0x141e4d890`'s out flag → Positioned `+0x1e5 & 4`). A path the expansion limit cut short is walked to its end and then
paths again from there at once (`0x141e7bec0` → `0x141e7b920`). Starting a non-pathing action ends the Move with
`0x141e90740(…, 0)`, which leaves the pathing running, so a ranged monster shoots while it walks on to the Move's target
(on a lockstep client too: §6).
Monsters far from the player keep their walk but do not tick. Their speeds vary by a few percent (7.60–8.40 for a base of
8): the Actor's run-speed roll `+0x1a8`. Actor
setup `0x141e9ca20` seeds a TinyMT32 state (`0x1413cbe50`, init_by_array) with the key [object id, the object type's
u32 at its data +8] and, when the MonsterVarieties row's two unnamed i32 columns before `AilmentThreshold` (`__col75`,
`__col76`) differ, draws a uniform integer in that range (`0x1404d03d0`); the roll multiplies the Pathfinding speed by
1 + roll / 100. Of the 10 691 rows, 8 694 have (0, 0), 875 (−10, 10) and 855 (−5, 5) (every plain Goatman: 8 × 0.95
to 1.05 is the band measured), then (−5, 10), (0, 10), (−30, 15), (−5, 8), (−15, 15). The server draws the same roll
(`MonsterSpeedRoll.cs`, `AnimationVariantSelector.NativeRandom`) with the monster's 0x2b9 type hash as the type word
and walks its estimate at that speed (the Gateway's create log prints the speed and the roll). Checked live
2026-11-03 (the lockstep tick trace, each walker's step times): four Drowned walked at 8.16, 7.92, 7.60 and 7.84
cells/s, the Gateway's rolls of +2, −1, −5 and −2 % for them; Hungry Corpses at 8.000, Sand Spitters at 20.000, the
player at 37.000. `scripts/re/frida/trace-movement.js` prints each monster's seed words, range and multiplier.
Equal bounds skip the random draw but retain that percent (`0x141e9ccb2` → `0x141e9ccc2`–`0x141e9ccd9`).
The extracted nonzero cases are AbyssCurseBot/AbyssDebuffBot (30/30, factor 1.30) and SareshFleshPillar
(−20/−20, factor 0.80). `MonsterSpeedRoll.Percent` preserves these fixed values; zero/zero remains zero.
Prepared-client Actor setup readback confirms all three factors (float 1.29999995/1.29999995/0.80000001);
the server's corresponding unmodified-variety speeds are 68.9/68.9/0.8 cells/s.
Decompiled in the research branch's `re/server-gaps-3.29.3b.md` §3.

**A melee monster's chase.** GGG repeats the monster's attack aimed at the player object about every 200 ms, near or
far (flags 0x80, the position pair is where it has the monster and the player); Move is for everything that is not a
chase. A server-created attack that paths is not played at once: the action walks the monster up to its target, as far
as the attack's reach (MoveTo with max range reach − 1 and the target's size as the tolerance, so distance ≤ reach), and
waits there until S→C 0x1ac sets the Actor's flag 0x10 (`0x141e94720`). The tick paths a waiting action again every
0.25 s. So 0x1ac is what starts the swing, and when it arrives first the swing starts on arrival. GGG sends it when its
own copy arrives, or with the 0x1ab in the same tick when the monster is already in reach, and that sets the
swing's timing on both sides. `skill_is_non_pathing` on the skill skips all of this: the action plays where the monster
stands, without a 0x1ac. The Gateway's melee skills no longer carry it, and its ranged skills and boss stages still do.
It chases the same way (`PlanMonsterAi`, `InstanceProtocol.ActionStartedNow`), and its estimate walks the same walk-up
with `PlayerWalk.Approach` at the search budget of §5.

Whether a walk-up has got there (a monster's chase, the player's attack on a monster) is MoveTo's goal test
(`0x141e7b260`) on the target's own cell: the action's target (`0x14123d600`) is the target object's Positioned cell
itself, so the goal follows it as it walks (51 of 51 traced setups of the player's actions on an object). It is tested
where the walker stands when the order applies (`0x141e7b530`), after the walker's steps in every tick (Pathfinding's
tick, `0x141e7bfb0`), and at every waypoint of its path it steps into (`0x141e7bec0`). Objects tick in ascending id,
so a monster tests the player's cell as his steps this tick left it, and the player a monster's as the tick before
left it (2 951 and 181 traced ticks). Met, `0x141e7bc20` stops the walk and fires
the arrival (`0x141e942f0` → `0x141e94720`): at a waypoint the walker stands at once, since the piece's step time is
spent; mid-piece the stop (`0x141e350e0`) waits for the next tick's steps. A blocked step reaches `0x141e7bc20` too
(listener table `0x1435a6728` slot 2, `0x141e7bf80`), which tests the goal again where the walker stopped: met, that is
an arrival; otherwise, as when a search finds no path, the action ends. At the path's end out of reach it searches
again from there toward the target's cell in the same tick (`0x141e7b920`). Mid-path out of reach it searches again
only for a target that has left the cell it last searched toward (`+0x550`), 2.0 s after that search, or 0.7 s after it
once the target is 64 cells from that cell (`0x141e7bfb0`: each tick adds its frame to `+0x55c`, which the search
zeroes); decompiled, not reached in the traces (the oldest search at a tick 0.76 s). `0x141e94720` also waits for the
actor to face its target, but only for an action with `action_required_target_facing_angle_tolerance_degrees` (slots
+0x230 and +0x238, `0x14123d650` and `0x14193ebc0`: 0 reads as a full turn); no granted effect, skill level or monster
variety has it, so no monster here waits for it.
A monster told to swing while the player is out of its reach does not stand: on the client it walks after the player
until it can swing. So on a predictive client the Gateway calls a swing off when the player is out of reach in its first
150 ms, and swings at a walking player only from 2 cells inside the reach, since the client's player is already farther
on. Both numbers are the Gateway's guesses, for a client it cannot see; a lockstep client's swing follows the goal test
itself (§6).

Each melee order has its own rising sequence with the top bit set, as GGG's do. The client picks the swing's clip by it,
and the hit lands at that clip's contact keyframe (`monster-contacts.json`). The swing plays where it began (Melee,
0xb188, at Actor `+0x1b0` from its start: 6 343 of 6 368 samples in 24 lockstep pack fights did not move, GGG's 12 867
of 13 153): whether it reaches the player depends on where he is at the keyframe, not at the swing's start. The
Gateway lands the hit only within reach + 2 cells of the monster then (`CommitMonsterHitLocked`), a margin with no
source either.

**The player's own walk-up.** The client's action goes on until the target is in reach and needs no word from the
server to swing. A predictive client's paths again every 0.05 s for a player (0.25 s for a monster, `0x141e8eb60`, which
`0x141e8ef50` runs again only in that mode), and the Gateway's estimate every 100 ms, for up to 3 s
(`FollowMonsterToSwing`; neither number is the client's). A lockstep client's is the walk-up above, set up once by its
order (1 446 of 1 447 traced setups of the player's actions came in the frame of an order's apply). In reach means the
walk-up's own goal: the distance less the target's size within the weapon's range less one (WeaponTypes RangeMax: a bow
or wand 120, the Rusted Sword 11;
`0x141e8eb60` gives MoveTo `+0x168` − 1 as the max range and `+0x158` − 1 as the min (a 0 counts as 1), so the client's
search runs with max range 119 for the bow and 10 for the sword, live 2026-11-03), with the line to the target. MoveTo
(`0x141e7b530`) first tests where the player stands (`0x141e7b260`): in range, and the line to the target on layer 1
(the projectile grid) holding the action's precision (`0x141e8eb60` → MoveTo `+0x528`: 3 for the bow's attack, 1 for
the sword's and for a Move) up to the target's body: the last cell at or above the precision within size + 1 of the
target (`0x141d87500`). Met there while a lockstep client's player walks, the action goes on to the swing at once:
`0x141e7bc20` puts the stop off until the frame's steps (`0x141e350e0`) but tests that same cell again, so the player
swings from where the steps leave him even when they carry him out of reach, walking away from the target
(`PlaceLockstepAttack`). Failing that, the search's goal is the first cell in range whose whole line to the target holds
the precision (the pathfinder above): the bow's attack walks to where it can shoot (in reach at 35 cells on the Strand the client
walked 17 cells round a rock, then shot). When the ellipse holds no such cell the search returns nothing, and the goal
is tested where the player stands at precision 1, which the order then keeps (`0x141e7b920`): a line without a 0 lets
the shot go from there, a wall in it leaves the action without a path. The Gateway does the same
(`ClientPathfinder.InReach`, `PlayerWalk.Approach`'s precision, `Combat.SwingInReach`) from the capture's
`.projectile.bin` or the generated level's grid (`AreaLayout.Sight`). Live 2026-11-03 on the Strand: ten bow shots
and one wand shot at Sand Spitters behind a rock's band each walked 10 to 14 cells on the client, and the Gateway's
cell equalled the client's at every following start. The wand's precision is the bow's (traced); an attack gem's is
taken to be (not traced). Without a projectile grid any cell in reach is the goal, as before.

**Stun and freeze.** The client stops for a hit's recovery (0x1d1) and then goes on with its walk, so the server holds
its walk for that long (`PlayerWalk.Pause`) instead of ending it. A held walker is not moving for anything that asks
(`PlayerWalk.Moving`). A click in the meantime starts when the recovery ends, and only the newest counts (`ReleaseHeldMove`). The client sends
nothing for a skill clicked early in a hold, buffers one clicked in about its last 0.4 s, and sends that as its own hold
ends, up to 33 ms before the server's on localhost; the server waits those out. The client does not
stop for `is_frozen`, and `virtual_action_speed_+%` at -100 would divide its clips by zero, so a freeze plays a recovery
of its own length (`ShowFreeze`).

**Movement skills.** The client limits the target itself before it sends the 0x25 (Flame Dash lands where the server
does). A target past the walkable ground ends where the line to it does. Aimed at a monster it lands on the monster, by
the 0x24 path. Whirling Blades and Dash do not stop at the target: they travel 40 and 33 cells along the aimed line and
stop at the first cell they cannot walk (no stat carries either number). The client can still refuse a skill the server
has landed (a Lightning Warp often), so a resync follows at the end of the action, and every other resync but a stun's
waits for it: until then the server has the player landed and the client has it on its way. ActiveSkillType Movement also
covers skills that leave the player where it stands when used (Mirror Arrow, Smoke Mine, Phase Run, Chain Hook, Withering
Step, Ambush), and channelled ones, which report their own way: none of these moves the estimate (`Game.LandsAtAim`).

**Ranged monsters** keep Move orders and `skill_is_non_pathing`: on the attack-aimed chase the client walks a Sand Spitter
well inside its reach of 30, so its stand-off needs the server's slots.

**Pickups.** The client's walk to a ground item has max range 20 and tolerance 1 (traced live with real clicks), so it
ends on the first cell within 21 of the item.

**Blockers.** Totems and the tutorial chest block paths as the client's other objects do.

**Server side.** `PlayerWalk` runs `ClientPathfinder` from the player's cell on every order and steps the result at the
client's step times; walk-ups to NPCs, stashes, waypoints and area transitions use the client's range
(`PlayerWalk.WalkUpTo`, `InWalkUpRange`); the blockers are the NPCs and stashes the Gateway created (`ClientState.SetBlocker`), the living
hostile monsters where the server's estimate has them (size MonsterVarieties `ObjectSize` − 1, not checked live), and the
quest walls' cells. Replaying a recorded session (424 client path calls, 2026-10-21), every path matched the client's
waypoint for waypoint. At each order, the predicted cell was exact 184 times, within one cell 392 times, and at most 2.8
cells off. The rest is the client's frame timing, which the server cannot see. A second session (2026-10-21, town and The Coast,
speeds 37 and 370) matched all 940 town paths; on The Coast 569 of 614 matched without monsters, and the other 45 bend
around monsters (hence monsters as blockers). Held-mouse walks drift by a few cells (town, 37 cells/s: median 1, max 7;
at 370 cells/s one 16 ms frame is ~6 cells): the client applies a click from its previous frame, the server when the
0x25 arrives, so the timing differences add up while the button is held and vanish when a walk ends on its target. With
the walk-up arguments, all 246 town paths of a third capture match, NPC, stash and waypoint walks included. Monsters
walk on `PlayerWalk` too. `Approach` (a melee target, an item pickup, the map device) keeps a reach in cells.

**Speed changes mid-walk.** The client restarts the segment in progress from its cell at the new pace at once
(`PlayerWalk.SetSpeed`).

**Passive movement speed.** The passives' `base_movement_velocity_+%` are summed into `CharacterStats.Pools` and the
walk estimate uses `37 × (1 + pct / 100)` cells/s; the same total is sent as the client's `movement_velocity_+%`, so the
client and server walk at the same speed. `ApplyMovementSpeed` runs on login, level, passive changes and area changes;
`/set_speed` adds its percentage on top of the passive total.
Only unconditional `increased Movement Speed` passives are modelled: action speed, more multipliers, gear/buff
`movement_velocity_+%`, conditional nodes and the `movement_velocity_cap` constant are not applied yet.
`movement_speed_cannot_be_reduced_below_base` bounds the total below by 0 (PoB `max(MovementSpeedMod, 1)`).
The client's lower bound for `movement_velocity_+%` is 0 instead of -100 from the same raw flag (`0x14251cbd0`).

**Compact int** (writer `0x141fa3c50`): signed, 1 byte for −0x40..0x3f (value & 0x7f), else 2/3/4 bytes with first-byte
prefixes 0x80/0xc0/0xe0 carrying the top 6/5/4 bits, else 0xf0 + 4 bytes; big-endian (`InstanceProtocol.CompactInt`).

**S→C 0x1ba** move (class `0x1435dd7b8`, read `0x141fe0ff0`): header, `i32 x, i32 y, u32 +0x28, f32 speed, u8`; the
Positioned handler calls `0x141e34120` with duration = distance / speed. With speed 37 it overrode the client's own walk
and crawled (~1 cell per 1.5 s), so it is not sent for the local player (`Game_MOVE_MODE=move` to test).
**S→C 0x1b7** teleport: header, `i32 x, i32 y, u32 → +0x290, u32 → +0x2b4`; works (`Game_MOVE_MODE=teleport`).

**Resync: S→C 0x128** (`InstanceProtocol.Resync`, `Resync.cs`): compact count, then u32 object, u32 x, u32 y in grid
cells, big-endian, with the player first. Ignored in lockstep. When the player is 20 or more cells from the first
record the client puts it there, and then every listed object on its record; otherwise only the objects 20 or more
cells from theirs. Under 20 cells nothing moves, so sending one costs nothing. Live 2026-10-31: the player jumped 30
and 25 cells onto the records and went on playing. GGG sends it with every stun of the player and with no other hit,
listing the player and the monsters within 23.4 cells; the Gateway does the same.

GGG sends no timed 0x128: every drift above 20 cells it leaves alone, and a timed one is seen as objects, the player
included, jumping 20 cells or more. A gap closes when a walk ends on its target.

In a crowd the two copies of the player can part for longer. A monster's estimate a couple of cells off closes a narrow
gap, so the server's player stands while the client's walks on, and the server's monsters stay round the one it has
(the reverse happens too). GGG's do the same: in The Mud Flats its two stood up to 93 cells apart, and only a stun's
0x128 or the client's own 92-cell snap brought them together. A closed ring holds both copies, a few cells apart. From
the server the two cases look alike, so the Gateway leaves bodies blocking; lockstep (§6) has no second copy to part.

**The wake zone.** Monsters more than about 171 cells from the player leave it (S→C 0x1aa) and stop ticking on the client,
so a chase the server gave up does not run on there. They come back with 0x1a9 and a 0x128 for their cell (`KeepWakeZone`).
The server's copy stands out there too: its walk ends where it is and the AI gives it no order until it is back. Chased
on instead, an estimate walked 162 cells while the client's copy stood frozen, and the return's 0x128 put the copy there
in one jump (live 2026-11-03, a Rhoa pack outrun on the Mud Flats: three such jumps of 95–162 cells in one packet).

**The client asks for it.** Each commenced action other than Move adds one to a counter, each 0x1ac takes one off and
5 s take one off; above 3 it sends C→S 0x16 command 8 (`InstanceItems.ForceResyncCommand`) and starts again. The
Gateway answers with a 0x128, and `/oos` does the same by hand. GGG ends non-Move actions with a 0x1ac in the same tick,
so its clients do not ask, and the Gateway does the same.

**Measuring it.** `Game_TRACE=sync` logs the server's estimates every 50 ms, `scripts/dev/sync_drive.py` plays a session
client with real mouse input through `scripts/re/frida/drive-input.js` and records where the client has every object
(`trace-sync.js`), and `scripts/re/sync_report.py` compares them. The bridge's `move` is no use for this: it only writes
a 0x25, so the client predicts nothing. `skill-clicks` plays the skill on the right button or a key, and
`scripts/re/frida/trace-monster-actions.js` shows when the client takes, starts and stops other actors' actions.


### Living actor clearance

`LivingCollision` uses `MonsterVarieties.ObjectSize - 1` as the positioned footprint, including unique bosses.
Negative footprints do not block; zero is a valid small body.
Monsters with a negative footprint do not avoid living bodies.
`AreaLayout.WalkableFor` retains the captured terrain clearance values instead of treating every actor as player-sized.
Monster orders use the bounded native pathfinder with the client's budget: 1000 expansions for a
unique, 100 for any other monster (`0x141e4d890`).
Their blockers include stationary objects, other living monsters, the local player and published instance players.
Only chasers on Move orders use surround slots: ranged monsters and the player's minions.
Surround candidates, including retained slots, must clear the player's Chebyshev footprint.
The circular ring alone is insufficient: its diagonal cells can lie inside the player's blocker for larger monsters.
Inner slots must also lie within the monster's attack reach when space permits.
Stationary blockers and published players are excluded from candidate slots.
The native path budget can end a valid route short; an arrived chaser retries at most twice a second.
Each AI/collision pass owns one combat-locked body snapshot and indexed self-exclusion views for all mover paths.
Moved bodies update the index before later movers use it.
The indexes are discarded after the pass; normal/unique expansion budgets, stationary hulls and phasing remain intact.
AI and correction passes advance prior projectile contacts before taking the body snapshot, using the shared
projectile mutation scope and its experience-before-combat lock order.
The client stops a walker at the first cell a body blocks (`0x141e32710`, `0x141e47a40`), whatever its path said, and a
walker that started inside a body steps through until its first free step. `StepWalkers` does the same to the server's
estimates, every 50 ms and before each AI decision (an order would drop the steps it replaces): the steps taken since the
last pass, the player's and every monster's in time order, each against the cells the others hold at that moment; on a
lockstep client frame by frame (`StepFrames`), each walker's steps of a frame in ascending object id against the cells
as moved so far, as its world ticks (§5, the lockstep tick), whole frames up to the start of the frame being sent. It
sends nothing. A chaser's next order paths it again, and a path the search cut short paths again from its end. Nothing
moves a monster or the player out of a monster: the client does not, and a server that did so walked its copy away from the client's (it used to send
ActionEnded and Teleport, then to teleport only its estimate, and to walk the player out through a crowd that held the
client).
The client never snaps a non-predicted monster to the 0x1ab position pair by the 92-cell check, which runs only for
sequence ≥ 0. It does snap one that is 46 or more cells from the pair when the action starts. The overlap test reads
every body's cell once per frame, and `BlockerSet` buckets the blockers by cell so `ClientPathfinder.ObjectBlocks` reads
only those that can reach the queried cell (same answers as the full scan).
The player's own path treats estimated monster bodies as blockers too, so a player the client finds boxed in stays in
on both sides.
Alive friendly monsters also block; corpses and removed objects do not.
S→C 0x2b0 tells the client the same about a corpse, about 165 ms after its life reached 0, as GGG does.
The client takes it live; what it changes in the client's own paths is not measured.
Phasing removes living bodies from the local player's path checks.
Large bodies are checked even when their centres lie outside the small query's spatial buckets.

`InstanceState` publishes immutable player snapshots without acquiring another connection's monster lock.
Each connection has a distinct remote object id in the `0x20000000` range; its own player remains `0x1000`.
Observers create a normal Player object without local-player selection, update movement/life and remove it on departure.
Ordinary pose updates use 0x1ba; large coordinate jumps use 0x1b7.
Arrival overlaps prefer moving the later peer, avoiding two actors choosing the same escape cell.
This provides body collision for players already sharing an instance, not a party manager or shared monster simulation.
Native checks: normal zombie clearance 4 cells, unique Hillock clearance 5 cells; removal frees the target cell.


## 6. Lockstep (not served yet)

Everything above is the client's **Predictive** network mode (Options → Game → Network Mode;
`[NETWORKING] networking_type=predictive` in `Documents\My Games\Path of Exile\production_Config.ini`; the mode is
`+0x984` of the world: 0 predictive, 2 lockstep, 3 auto). The client alone chooses it: no message sets or reports it.
"Auto" starts at lockstep (`0x14449e7f0` holds 2), drops to predictive when connecting took over 90 ms and returns under
70 ms (`0x142148880`), so it picks lockstep on localhost. A fourth value, `hybrid`, is disabled in the client.

`Game_LOCKSTEP` decides who is served: `1` a connection confirmed as local, `network` any connection, unset nobody
(`LockstepLink`). A client not served gets a message box asking for Predictive (once per connection). A predictive client
plays predictive whatever the setting. Latency adds input delay, not position error: both sides count the same frames,
so a client on a slower link plays the same frames later.

A tick's u8 tells the modes apart early: only a lockstep world counts frames behind, so a predictive client's is always 0
and a lockstep one's turns nonzero within seconds, idle or not. Addresses cannot tell a tunnel on both ends from loopback,
so with `1` a client in lockstep is also timed (`LockstepLatency`), and with `network` it is timed for aiming only. S→C **0x194** (id only, handled on arrival) tells a client to go
AFK: it answers at once with C→S 0x16 command 36 and the text "This player is AFK.", and nothing else changes until a
server replies. The client reads its socket once per update, so one round trip says little on a slow client (about 100 ms
on loopback with the window in the background), and the lowest of the last 16 is the network's. The bar is the client's
own: under 70 ms, over 90 ms. Requests wait for the first verdict. A character keeps its verdict across area changes for
30 s. A connection that slows turns off within about 30 s, and back on when it recovers.

**Server frames.** In lockstep the client's world does not run on its own clock: it runs **server frames**. S→C
**0x2b6** (u8) sets the frame length in ms (`0x141645fa0` stores the zero-extended byte as u32 at world `+0x11c`);
S→C **0x2b5** (id only) ends a frame. In nonzero mode, after activation sets world `+0xa00`, the embedded message
intake (`0x1421872b0`, this=world `+0x1c8`) appends a null delimiter to the record vector (`+0x9e0`) and queues
the current frame length (deque map at world `+0x9c0`, count `+0x9d8`). Before activation it adds neither.
Each client update adds its time to world `+0x988` and runs every queued frame it covers
(`0x142183930` → world vtable `+0x360` = `0x142183ce0`). Nonzero-mode core updates apply initial ordering-key-zero
messages, then interleave eligible messages with object/component checkpoints; objects use the frame's delta.
The common header's ordering object/checkpoint fields control those insertion points, independently of the update
target. See [World message inputs](../re/projectiles/world-message-inputs.md) and
[component checkpoints](../re/projectiles/world-scheduling.md#10-component-checkpoints-can-consume-ordered-incoming-records).
Behind by 1, 2, 30 or 60 frames, it speeds up × 1.01, 1.5, 2 or 5;
the most frames it was behind since the last tick goes out as the u8 of C→S 0x1b (world `+0xb98`,
"lockstep_lag_frames"). Without 0x2b5 nothing runs: the player is created but never drawn. A predictive client ignores
0x2b5, and 0x2b6 only stores its byte, so the Gateway always sends them (`RunServerFrames`, 33 ms, `Game_FRAME_MS`):
a lockstep client is then drawn and its world runs. Found with hardware watchpoints on those fields.
On Windows a timer wait ends on the clock tick, 15.6 ms unless the
process asks for less, and Windows 11 drops the request of a process without a window (every launcher starts the
Gateway so) unless it opts out of that power throttling ([headless clients](../dev/headless-client.md) set the
opposite). Without both, lockstep frame ends went out 0 to 16 ms late (31 and 47 ms apart). With them, while a player
is in game (`WindowsTimerResolution`), and `RunLockstepFrames` woken at each frame end, 77 to 93 % went out under 2 ms
late (on loopback, in town and The Mud Flats); its earlier poll every third of a frame sent them 22 and 44 ms apart.

**Client moves** (`0x14215f700`, `0x14215fb10`, `0x14215fcb0`, `0x14215fe50` send them when `+0x984` is 2):

| Id | Bytes | Fields |
|---|---|---|
| 0x018e | 15 | use a skill at a location: i32 dx, i32 dy (the target relative to the player's cell), u16 skill id, u16 action flags, u8 (`018E 00000029 FFFFFFEF 2909 0402 00`) |
| 0x018f | 11 | use a skill on an object: u32 object id, u16 skill id, u16 flags, u8 (instead of predictive 0x24) |
| 0x0192 | 10 | the held skill's new target, every 69 ms while the button is down: i32 dx, i32 dy |
| 0x0195 | 2 | release |
| 0x0196 | 4 | u16 skill id: release that skill |

The client creates no action of its own: nothing moves until a 0x1ab comes, for the player as for a monster, because
Actor `+0x1c8` is set only outside lockstep (`0x142156d80`). What else changes in the client:

- Each action start puts the actor on the 0x1ab's position pair at any distance (`0x141e8e7e0`; predictive: 46+ cells).
  That is what holds the two copies together; between action starts the client still walks its own.
- 0x1ac does nothing (the handler returns unless `0x14164dc70` gives 1), and a queued action starts without waiting
  for one (`0x141e94720`). 0x128 is skipped (case 0x50 of `0x14214a190`).
- S→C **0x1ad** (object header, u16 sequence, i32 x, i32 y, `InstanceProtocol.ActionTarget`) reaches only an active
  channelled action with that sequence. The sequence is the server's own, so a walk gets a new 0x1ab instead.
- A skill's pay check runs for every actor (`0x141e9d930`, `0x141e9dc10`), not only the local one.

**What GGG sends a lockstep client.** What it sends a predictive one, including 0x1ac and 0x128, which do nothing here;
no 0x2b6. A click gets its 0x1ab with key 0 at the next frame start. Its target is the client's cell when it clicked plus
(dx, dy), that is, where the cursor was, not the server's cell by then. 0x192 only
updates a held target: the player's own tick starts the Move (key = the player, u16 0x9001), about one per 0x192 while
walking and one per frame from the same cell while blocked. 0x195 gets no answer and does not cancel a target not yet
started. Sequences go up by one, top bit set. A click during a swing waits (it comes with flags 0x0001, u8 3), and the
newest starts when the swing ends. A skill held down repeats from the player's tick, once per action, aimed at the newest
0x192, until 0x195; an attack on an object that dies goes on at the ground. A channelled skill gets a 0x1ad for a new aim
and a 0x1ae (the sequence, u8 1) on the release. Instant skills keep 0x190 and 0x191, answered with 0x1ce as in Predictive.

A skill asked on a monster (0x18f) gets its 0x1ab at once, from the player's cell and aimed at the monster, as an object
use does: the client's own action walks up and swings when it gets there, as a monster's chase does, and the held skill's
next use follows that swing. The Gateway does the same (`LockstepAttackStart`) with no second 0x1ab: it places the
0x1ab, and the walk-up with it, at the start of the frame it goes out in, and its step pass runs that walk-up with the
monsters' (§5, `StepWalkUp`). The swing begins in the goal frame (46 of 46 traced). No timer of the player's own swing
was found (Actor `+0x26c` stays 0 for him), so his is dated as a monster's, whose attack time is taken as its swing
starts (`0x141e95e60`): the hit, a shot's release and the next attack from the goal frame's start, the Move cut-in from
its end, all at the attack time the swing begins with. An object use (a pickup, a chest, a quest object) likewise gets its one
0x1ab from the frame pass, and its handler sends none of its own (`UseSkillOnObject.Started`): the handler's second
start put the client back on its from cell and had it path again, two cells and a frame lost on every pickup (live
2026-11-03; the client's pickup walk, max
range 20 and tolerance 1, otherwise ends on the Gateway's stop cell, 21 cells from the item, 0.07 s after the Gateway's
pickup). Before 2026-11-03 it answered only at its own swing, with the walk-up's end as the position: the client
played its last Move meanwhile and was then put on that cell, up to 35 cells away (live, a bow at 33 cells), and the held
skill's repeats came at the attack time from the request, each cancelling the walk-up before it. Live after: 18 bow
attacks in 52 s started at most 3.2 cells off the client's cell, none cancelled.

A held attack whose target died goes on at the ground, and the default attack at the ground swings where the player
stands: GGG's player stayed on its cell through 101 such attacks aimed 18 to 29 cells away, one per attack time. The
Gateway stands for it on a lockstep client (`Messages.cs`, `inPlace`); it walked there before, which put the player on
the dead monster's cell. A predictive client sends a Move for that click instead, so nothing changes there.

A Move whose target is within two cells (Chebyshev) of the walker's cell is not taken by the client at all: no path
search, no step and no move onto the 0x1ab's position pair, even when the pair is not the walker's cell, and the action
the walker is in goes on (the Move before, under a shot too). The test is the admission of `Move` and `MoveSpecial`
(vtable slot +0x130, `0x1419442d0`): dx² + dy² ≥ 9 from the walker's own cell to the target, which `0x141e8e060` runs
before it ends the current action and before `0x141e8e7e0` sets the pair; a refused Move is destroyed there.
`MoveForced` (0x3f1f) has no such test. Every Move 3 or more cells away searched a path and took the pair first. Live,
with the 0x1ab apply and the path search hooked: the player 308 of 308 such Moves in two near-Move fights (2026-11-04;
127 of them with a pair other than the client's cell), monsters 317 of 317 in six pack fights, and 8 244 of 8 244
farther monster Moves searched. The Gateway sends no such Move to a lockstep client on any monster path (chase legs,
surround slots, a flight, a minion's approach, a Rampage weapon chase), and where it drops one the estimate keeps the
walk it had. A melee leg that short goes as the attack aimed at the player instead, and a minion's approach as its
attack aimed at the target: a melee one walks up and swings in the frame it gets there, an in-place one plays where the
minion stands. A give-up and a freeze send nothing, so a chase's walk-up runs on. A Rampage weapon chase that short is
dropped at each retry, so a monster whose reach is under 3 cells (only the two Blood Swarm varieties,
`LeagueAzmeri/PlagueSwarm`, reach 2) can stand short of the weapon until the weapon moves off. Sent, a monster's left
the client walking on and preceded 76 of
126 monster starts 2–3 cells off. The player's used to stop the estimate while the client walked on. A near Move still
replaces a queued Move that can be cancelled (its vtable +0x190 and +0x208, flag 0x1000): the offer
(`0x141e8cf10`) destroys that one before the test runs. A queued non-Move it replaces only when it carries flag 0x400
and the walker's Player component, if it has one, has +0x2e9 other than 2 or 3; otherwise the offer refuses the Move.

A Move that reaches the client during a swing is taken (`0x141e8cf10`) but queued: the actor runs it when the clip's
`lock_cancelling` keyframe has played, or its contact keyframe for a clip without one, and only then (live 2026-11-03,
the first Move the client ran at once: a wand's 0.334 s into a 0.667 s attack whose clip contact is at 0.300 s; a
one-hand sword's 0.58 s into a 0.645 s attack whose contact plays at 0.56 s; Moves 0.40 to 0.57 s into the sword's swing
all ran at 0.60 s; a bow's 0.40 s into a 0.714 s attack, its clip's `lock_cancelling` at 600 of 1066 ms with the
contact at 399: Moves 0.37 to 0.40 s in ran at 0.40 to 0.43 s). So the hold after a swing is that keyframe as the
client plays the clip ([11](11-skills.md) §4: a one-hand sword's contact lands late in the swing, not at the attack1
keyframe's fraction) plus the client's two frames (`LockstepActionTail`, `LockstepAttackCutIn`), for a swing at a
monster and at the ground alike. A held attack's next use, at a monster or at
the ground, waits for the attack time of the one before (`NextAttackAt`): one sent as soon as the player was free came
0.4 s into a 0.645 s swing and was queued to its end, with the server's swing and hit ahead of the client's by as much.

A melee monster's chase order (an attack aimed at the player) swings by itself on a lockstep client in the tick its goal
test passes (§5, "A melee monster's chase"), since in lockstep `0x141e94720` waits for no 0x1ac and sets no 0.25 s
repath: in the order's own frame when the monster stands in reach as it applies, else in the frame its own steps
or the player's bring them within reach. With the player gone on, its action goes on after the live player, pathing
again until he is in reach (per-frame traces of six fights: 85 of 85 walk-ups that came within reach). The Gateway
runs the same test, at the order's apply (`TakeLockstepChase`) and in each frame of the step pass against the player's
cell as his tick left it (`StepWalkUp`), and takes the swing as its own from that frame's start, with no order
(`MonsterAiPlan.ArrivedSwings`): a second one only waits behind the swing (`0x141e8cf10`), and GGG's next 0x1ab to
the same object after its 0x1ac comes 0.96 s later at the median. The swing runs the whole frame it starts in: its
timer, Actor `+0x26c`, set to the attack time on the way into the swing (`0x141e95e60`) and counted down by the actor's
tick (`0x141e8ef50`), is one frame below the attack time at that frame's end (48 of 48 walk-up swings). A lockstep
melee monster standing in reach gets the chase, not a swing order; one that never walks (MovementSpeed 0) gets no chase
and keeps the swing order. A ranged monster's
in-place attack leaves its Move running on a lockstep client as on a predictive one: per-frame, the shot's clip starts on
the order's frame and the Sand Spitter walks on to its Move's target (377 shots ordered mid-Move: 1.4 cells left at the
median, 4.2 at the 90th percentile, 7.6 at most), so the estimate keeps its walk; where its path is longer than the
client's, it walks on alone (one 18-cell case in eight pack fights).

A monster in a 0x1d1 TakeHit's recovery starts no action (Actor `+0x219` & 2, tested by the action gate
`0x141e8d900`). An order that comes meanwhile waits (`0x141e8cf10`), and the monster's own tick starts it in the frame
that ends the recovery (`0x141e934b0`), after the player has taken that frame's steps. That frame comes as many frames
after the 0x1d1's own as the duration needs at 33 ms each: 31 for 1.0 s, the only duration in the bench data (orders
sent 30 frames after it still waited after their own frame's step; none of 114 sent 31 after did). A chase sent in that
frame, with the player in reach at its start and out of it after his step, walked up where the server took a swing
(bench 2026-11-05). So the Gateway orders a recovering monster from that frame on (`LockstepRecoveryEnds`), and a
chase in it takes the player's cell at the frame's end (`LockstepChaseTarget`). A chilled monster's count and a
TakeHit the client refuses are not traced.

**Keeping the estimate's walk the client's.** The client's walk is exact in time (§5, the lockstep tick), so the
estimate's steps fall in the client's frames as long as the two start a walk in the same frame with the same phase. The
step pass replays whole frames in object order (`StepFrames`), and a monster's order places its walk when the 0x1ab
goes out, in the send gate, at the start of the frame being sent, after the steps the walk the client still runs takes
in the frames before it (`LockstepOrderFrom`; the frame itself advances in the frame end's gate): placed by the plan, a walk whose
0x1ab a frame end overtook (the AI pass and the frame loop are separate tasks: 14 of 1 193 chase orders in a traced
fight) started a frame early on the server. The chase walk-up such an order replaces runs on in those frames, goal
test included; when it swings there, the order is not sent, as it would only wait behind the swing (`0x141e8cf10`).
What the server cannot see
is the forced set: an order whose from cell is not the client's cell puts the client's monster onto it and zeroes its
accumulator, while the estimate keeps its fraction of the step in progress, so from then on the client steps that
fraction later, the next order finds the cells apart again about half the time, and the estimate runs a step ahead
through runs of orders (two of five orders after a set found the cells apart in a traced fight, against one of eight
after a match; 20 to 26 % of a fight's monster orders on a moving monster had the estimate a cell ahead). The Gateway
therefore orders a walking monster again only at a frame start right after the estimate's step (`LockstepReorderDue`:
its fraction under a frame; the order waits at most two steps past its cadence; the chase cadence's median stays
0.25 s, one chase in ten waits 0.5 s), so a client a cell off loses under a frame of phase. Pack sets after (bow,
4 × 40 s): orders on a moving monster with the client a cell from the estimate 18 % for chases, 12 % for Moves and
14 % for a Sand Spitter's shots (25, 22 and 23 % before; the frame-grouped pass alone changed none of them), the from
cell equal to the client's cell at 83 % of monster orders (77 %), the estimate's cell equal to the client's in 91 % of
monster frames (79 %). What is left is unobservable from the server: a one-cell
difference in the player's cell or in another body's changes a chase path's corners (the first segment's step time
differed on the two sides at 11 % of matched chase orders), a step then lands a frame apart, the set fires and the
phase offset it leaves (under a frame now) persists until that walk ends, since a matched order rescales each side's
own phase. GGG's server has none of this: it is the client's own simulation.

**Where the Gateway differs from GGG**, on purpose (`Lockstep.Differences.cs`, `LockstepLink`; `Lockstep.cs` and
`LockstepHold` follow GGG):

| Here | GGG | Why |
|---|---|---|
| Client and server can differ between action starts | Never: its server is the client's simulation | The server is a port |
| Off unless `Game_LOCKSTEP` asks, and local only with `1` | Any connection, any latency | A port corrects the client at each action start, so serving a network is the server owner's choice |
| The client's cell for a target is estimated: the lowest round trip and two frames ago | Its frames say which it was | A lockstep request carries no frame |
| Sends 0x194 to time a lockstep client | Not seen in any session; by its handler, an idle timeout | It is the one message found that the client answers by itself, and nothing else tells a tunnel from loopback |
| Frames laid over a clock, late ones sent at once | Frames are ticks of its world | The walks run on the clock |
| Orders a walking monster again only at a frame start right after its estimate's step | On its cadence, from its own simulation | A set after an order whose from cell is off zeroes the client's step accumulator while the estimate keeps its fraction; right after the estimate's step there is under a frame to lose |
| Sends 0x2b6 | Never | The client's default is not traced |
| Puts a monster back on its cell with 0x1b7 when it returns to the wake zone | Nothing: out of the zone it stands on the server too | Ours stands out there too now (its walk ends at the 0x1aa, no order until it is back); 0x128 is skipped here, so the return places it with LockstepPlace |
| No 0x1ac when a monster's chase walk-up starts its swing | One when its copy starts the swing | A lockstep client does nothing with a 0x1ac; the swing starts by its own goal test |
| A monster that gives up a pursuit gets no order: it goes on with its last one as the client's does, a chase's walk-up after the player | Nothing then either, but idle orders after (131 pursuits ended with the player 60+ cells off: a Move of 3+ cells 65, a self-aimed Wait 30, a flight 9, an aimed order again 15, nothing 12) | No idle AI here |
| A frozen monster gets no stop: its walk goes on, the estimate's too, a chase's walk-up with its goal test | Not traced | The client does not stop the player for `is_frozen` (§5) and takes no Move onto the walker's own cell; the player's freeze plays a recovery of its length (`ShowFreeze`), a monster's does not |
| An object use reports the player's cell as its target | The object's cell | The client takes the object's position anyway, and not every kind of object has a cell here |
| Moves in key 0 | Moves from a tick keyed by the player | Keys must arrive sorted, and the rest of the Gateway's sends are key 0 |
| A spell at the ground holds everything for its cast time | Not measured | A Move during a cast is not traced; attacks hold until their contact as GGG's do (`LockstepActionHold`) |
| A skill is echoed at any key 0, from the handlers Predictive uses | From the tick, keyed by the player | As for Moves |

Still to do: monsters' order keys, a trace of a frozen monster, and a walk set up in a stop's own frame and Shield
Charge's first step from a stand (§5). A movement skill's landing comes from the server's
own estimate; the client's snap at the next action settles any difference, and its 0x128 does nothing here.

## 7. More client messages seen

| Id | Bytes | Notes |
|---|---|---|
| 0x015a | 2 | id only, after entering |
| 0x01ef | 2 | id only |
| 0x0099 | 4 | `0099 0000`: two u8 (class `0x1435c0258`); meaning unknown. The 20 bytes once taken for it were 0x99, the two chat channel joins 0x134 ([10](10-chat-and-commands.md) §2) and 0x129 (id only) |
| 0x004e | 5 | `004E 00 2909`: u8 + Move skill id, after a click (skill released?) |

## Open

- Area transitions: The Coast exit (and other exits) are world objects the server has to create; none are sent yet, so
  walking to the town edge just stops there.

- What the server should do with the client's HNC packets (type 0xDD: a one-byte inventory checksum reply, sent when the
  client's item count differs from the server's 0xDC request).
- The meaning of 0x1ba's +0x28 and u8. Moving other (non-predicted) objects works with 0x1ab and the Move skill
  0x2909 (monsters, [10](10-chat-and-commands.md) §5); 0x1ba's float is distance per second (duration = distance / value).
- 0x0099 and 0x004e.
- Monster estimates still differ in the 5–50 ms until the client applies an order.
- The client's walk-up arguments for the map device (`Approach`).
- Two monsters racing for one cell can swap places between client and server: the same cells are taken, by different ids.
  Routes through a tight crowd differ for the same reason.
- A monster told to swing that the player then leaves walks after the player on the client while the server stands for the
  attack's time; the gap stays while both chase.
- Lockstep (§6): monsters' order keys.
