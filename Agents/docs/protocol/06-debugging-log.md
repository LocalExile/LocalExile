# 06 — Debugging log (2026-10-16, 2026-10-17)

Every live run against the patched 3.29.3b client, in order. Shows what was sent, what happened, how the cause was found
and what changed. Times are local. Logs: `Tools/re/out/live-{auth,gateway,patch}.log` and the client's own
`Client/logs/Client.txt`.

## Earlier session (summarized)

| Step | Result |
|---|---|
| Login handshake + 0x03 → 0x04 | worked with a Python smoke client, then with the real client |
| 0x04 with the account name in the wide string | client popped up a message box with the name. Now sent empty |
| no push after 0x04 | client waited. Sending an empty 0x1b moved it on to the character screen |
| 0x18 → 0x15 probes | mapped level (+0x48), class (+0x4a), portrait frame (+0x44), portrait (+0x46), red marker (+0x49) |
| 0x0e → 0x14 (port at +0x30, area 0x235c, zero key) | client connected to :6112 and sent 30 plaintext bytes (0x0F + 0x0E), then reset |

## This session

### 1. Opening decoded, server sends only 0x11 (11:25)
- **Sent:** `00 11`.
- **Saw:** the client echoed our random ticket (`0x74ebe7e7`) in 0x0F, accepted 0x11 and stayed silent for 45 s.
- **Learned:** after 0x11 the client waits for the server, and the ticket/key path works.

### Incident: a second client instance (11:22)
- A computer-use `open_application("Game.patched")` started a **second** client without `--patching-server`. It
  talked to our patch server, which still advertised the real CDN at that time, and queued downloads of
  `PathOfExile.exe`, `PathOfExile_x64.exe` and `gateway_list.txt`. It was killed before anything was written (file
  times unchanged).
- **Changed:** the patch server now advertises only `http://127.0.0.1:8080/` and serves only `gateway_list.txt` (side
  session, `Agents/docs/dev-server.md`). Rule: never start a second client; restore the running window with `ShowWindow`.

### 2. 0x1d area (11:30)
- **Sent:** `00 11`, then `AreaInstance(1, 0x235c, seed 1, level 0)` encrypted.
- **Saw:** "Client-Safe Instance ID = 1", "Generating level 13 area "1_1_town" with seed 1", tile/doodad hash, "[SCENE]
  Set Source [Lioneye's Watch]", then the first encrypted client message `007D 43E6886D 8EA8DD43` (0x7d = the two hashes).
- **Learned:** the 0x1d layout and the Salsa20 keying are right. The client waits after 0x7d.
- **How 0x1d was found:** the real-server log in `Client.txt` (February) has "Client-Safe Instance ID" and "Generating
  level"; `xr.py` on those strings → dispatcher case 6 → factory case 0x1d → write function `0x1421415c0`.

### 3. 0x22 + 0x2b9, no 0x21 (12:09)
- **Sent:** after 0x7d: 0x22 (player id 0x1000) and 0x2b9 with a static-analysis blob (parent components first).
- **Saw:** nothing (no reaction, no error, client stayed connected).
- **Learned:** world messages are queued until world activation (`world+0xb7a`), which only 0x21 triggers.

### 4. + 0x21, order "parent first" (12:12): crash
- **Saw:** WER crash, access violation at `0x141d7c5f0` (event log), in the spatial bucket insert `0x141d7c4c0`.
- **Cause:** with Animated before Positioned, Positioned read x one byte early (`00 73 00 00` = 0x7300 → tile 1280),
  outside the bucket grid (no bounds check).

### 5. Order as in Int.ot (12:21): crash
- **Saw:** access violation with fault offset 0 (a null call). No dump was kept and there's no `cdb` on the machine.
- **Changed:** switched to Frida through REA for the next runs.

### 6. Frida: exception backtrace (12:27)
- **Saw:** a first-chance C++ exception: blob reader out of range in the **first Life pool**. The client then showed a
  "Buffer Underflow" dialog (closing it ends the client).
- **Learned:** the components before Life consumed most of the blob, so the order or the component set was wrong.

### 7. Frida: component trace (12:29)
- **Script:** `trace-player-blob.js`, which logs the object's component vtables and the reader position around each
  deserializer.
- **Saw:** 12 components: Positioned, comp1 (`0x1421c2f40`), Pathfinding, comp3 (`0x141e114d0`), Life, Animated, Player,
  comp7, Inventories, comp9 (`0x142192a80`), comp10, comp11. Positioned read exactly 16; comps 1 and 3 swallowed 112
  bytes.
- **Changed:** blob rewritten in the traced order with encodings for comp1 (10 B) and comp3 (2 B).

### 8. (12:38): comp9 underflow
- **Saw:** Positioned 16, comp1 10, comp3 2, Life 55, Animated 1, Player 61, Inventories 2, all exact. Then comp9
  threw at `0x14219329d` (a u32 read after the main fields).
- **Changed:** comp9 now also gets u32 `+0x26c`, u32 `+0x274` and its slot-20 flag bytes.

### 9. (12:42): blob accepted, UI crash
- **Saw:** `CreateObject` consumed 167/169 (comp9's two conditional u8 are not read for us, so they were dropped next) and
  world activation finished. Then an access violation reading `0x161` at `0x1402d0a99`: the flask frame UI
  (`0x1402d0500`) with a null inventory from `0x141d621a0` (row 11 = Flask1).
- **Changed:** added 0xc0 (add inventory) for Inventories.dat rows 0–15.

### 10. (12:51): expanded inventory
- **Saw:** 16 inventories registered. Crash at `0x14012aba4` (null + 0xa0) after a lookup of row **0x54**
  (ExpandedMainInventory1).
- **Changed:** a scan of all 409 lookup call sites showed about 50 rows in use, so the Gateway now registers **all 117
  rows**.

### 11. (12:56): passive slots
- **Saw:** 117 inventories, no null lookups. Crash at `0x140785acf` (null + 0x18 → +0x30): UI `0x140785160` reads
  `instance+0xc2f8[instance+0xc2f0]`.
- **Found:** that slot array is filled only by `0x142155bf0`, called from the 0x3d handler for flag bits 2/3/4.
- **Changed:** added 0x3d with flags 0x1c.

### 12. In game (13:01)
- **Saw:** EnterArea returned and the HUD came up (life/mana orbs, flask bar, skill bar, minimap). Chat channels timed
  out. One C++ exception was caught internally (probably chat). The client sends 0x1b ticks every 100–500 ms.
- **Not right yet:** the camera shows open sea (spawn 115, 69 is not on land), and the HUD values are off
  ("Life 50/50 Reserved 50", "Mana 50/40 Reserved 40").

### 13. Login stuck after an auto-login (15:00)
- **Saw:** at startup the client auto-submitted saved login details; Auth answered result 5 and the client showed "The
  specified account does not exist." Typing the password and clicking Log In then sent nothing.
- **Learned:** result codes are BackendErrors rows + 1, and the client waits for the server to close a failed login.
- **Changed:** Auth closes the connection after a failed 0x04; unknown account → 5, wrong password → 6.

### 14. Walkability dump and land spawn (15:04–15:06)
- **Script:** `dump-walkability.js` (hook `0x1410ddcfd`). The terrain grid is 690 × 414, not 230 × 138 (that is the
  one-third texture). Layer a, value 5 = walkable.
- **Changed:** spawn (115, 69) → (197, 261), the deepest walkable cell. The camera now shows the beach and pier, and the
  minimap marker sits inside the town. The player model is still not drawn.

### 15. Life pools (15:08)
- **Changed:** pool = (current, reserved 0, …) and mana 40.
- **Saw:** "Life 50/50", "Mana 40/40", both orbs full, no "Reserved". The maximum comes from the client's stats.

### 16. Rising network ms → 0x1c (15:19–15:27)
- **Saw:** the overlay's network ms climbed forever. The latency is now − the oldest unacknowledged 0x1b.
- **Changed:** answer every 0x1b with 0x1c. With u32 = 0 the client showed **"HNC Protocol Violated"**: the u32 feeds a
  hidden, Salsa20-encrypted side channel that must decrypt to 0 when idle. With the HNC keystream as carrier the graph
  stays flat at ~120 ms. (A crash in the logger at `0x140df141a` came from the same exception.)

### 17. Invisible model → wake zone (15:31–15:47)
- **Tried:** Positioned "shown" flag; Animated with an explicit `Int.ao`; no missing files (Frida on `0x1421cc280`).
  None helped.
- **Found:** world 0x1a9 = ObjectInWakeZone (`0x141ec0aa0`). Objects outside the wake zone are not drawn or updated.
- **Changed:** send 0x1a9 for the player after 0x21. The Witch appears (washed-up pose).

### 18. Movement (15:36–15:52)
- **Saw:** a click sends C→S 0x25 (x, y, Move 0x2909, sequence, flags) and nothing more until answered.
- **Changed:** echo 0x1ab. With 0x1ba (speed 37) she crawled; teleport 0x1b7 jumped; the echo alone lets the client walk at
  its own speed and accept every click.

### 19. Teleported to The Coast exit and stuck (16:00–16:20)
- **Saw:** after a long click the player jumped to the click target near the town edge (unwalkable cells) and could not
  move. The 0x1ab position pair is the actor's position, not the target; the client teleports to it when it is ~92+ off.
- **Changed:** 0x1ab = (estimated position, target). `WalkEstimator` (A* on the walkability grid at 37 cells/s, fitted to
  recorded walks) keeps the estimate within a few cells of the client.

### 20. The Coast exit (16:33–16:42)
- **Placed:** `Metadata/MiscellaneousObjects/AreaTransition`, object 0x2000, at grid (384, 217) (the town room's
  "entrance2" point, R270 → (y, 413 − x)), destination HASH16 0x7ad2. See [08](08-area-transitions.md).
- **Run 1:** "Buffer Underflow". The trace showed the right component order but AreaTransition read 5 bytes (a compact
  int after the destination).
- **Run 2:** "Buffer Underflow" in MinimapIcon. ObjectMagicProperties read 4 bytes: a base-properties reader
  (`0x142201d70`) runs before its flags byte, called with the reader still in `rdx`, so the decompiler hid it.
- **Run 3:** all 41 bytes consumed, in game, and the exit icon shows at the east edge on the map. Using the exit is not
  handled yet.
- **Note:** computer-use hides non-allowlisted windows (the user's Chrome) when it takes screenshots. Verify through
  logs and Frida where possible.

## 2026-10-17: monsters (`/spawn_mob`)

### Incident: a client started without live.ps1 (12:05)
- **Saw:** `Client.txt` has a run (pid 13464) with web root `https://patch.poecdn.com/3.29.3.3/`, queueing
  `PathOfExile.exe`, `PathOfExile_x64.exe` and `gateway_list.txt`. Who started it is unknown (not `live.ps1`, which ran
  at 12:07).
- **Damage:** `Client/PathOfExile.exe` and `PathOfExile_x64.exe` were replaced by the current official files (unused:
  we run `Game.patched.exe`). Inside `Content.ggpk` the official `gateway_list.txt` came back, and the root PDIR hash
  was set to **zero**. `Game.exe`, `Game.patched.exe` and the other GGPK records were unchanged.
- **Symptoms:** with a zero root hash the client never logs "No patching required": `0x1428eb82e` requires
  `0x1421ccaf0` (local root hash ≠ 0) and the welcome hash to match, otherwise it asks for the root contents, which our
  patch server does not serve ("Timed out while waiting for packet from the patching server"). With the official
  gateway list the client then logged in to `ams.login.pathofexile.com:20484`, which failed the DSA check ("General
  Rhoa Failure #3") before any credentials were sent.
- **Fixed:** wrote SHA-256 of the root's child hashes back into the root PDIR (old bytes saved in
  `data/ggpk-root-hash.backup.txt`), then `GameServer.Patch gateway --login 127.0.0.1:20493` again.

### Monster without a level (12:17)
- **Sent:** 0x2b9 type `ObjectTypeHash("Metadata/Monsters/Zombies/ZombieBite")`, 198-byte blob, 0x1a9.
- **Saw:** "Not Responding", then an access violation at `0x14161d166` (Targetable's deleter, null component).
  `check-components.js` showed a C++ throw at blob position 0 from `0x141e6fd6f`, the Monster factory: "Creating
  monster with no level". The deleter crash is the cleanup of the half-built object.
- **Found:** the level is the `@N` suffix of the type path; S→C **0x20** registers `<Id>@<level>` per MonsterVarieties
  row (dispatcher case 9 → `0x141ec2540`). See [10](10-chat-and-commands.md) §5.

### Monsters in town (12:40)
- **Sent:** 0x20 (row 1159, level 13), then 0x2b9 with `ZombieBite@13`, three times.
- **Saw:** three Hungry Corpses next to the player, idle, tagged "Unidentified". Each blob: 11 components exactly as
  predicted from the .ot and schema.md, 198 bytes consumed, 0 left.

### More component lists, movement, AI (afternoon)
- **StateMachine:** two counts, then per `define_shared_state` a compact int + u8 and per `define_shared_timer` a u32 +
  u8, in .ot order. Frost Bishop, Arakaali, Haku: 0 bytes left.
- **Transitionable crash:** Alira and Gravel Eater hung the client after a fully consumed blob: access violation at
  `0x141ded7b9` in the animation-event interpreter. Cause: transition state 0. The post-create slot `0x1421c26f0` runs
  the event at index `state − 1`; states run 1..`num_states`. Sending `num_states` fixed it. Not sending the Animated
  path (tried first) was not the cause.
- **Movement:** 0x1ba slides an object in a straight line through walls; 0x1ab with the Move skill 0x2909 makes a
  monster walk, animated and pathed by the client. The monster AI chases with it.
- **Team:** the green hover outline came from Positioned's u16 team at `+0x1e0`, sent as 0 like the player's; 1 made it
  red (hostile).
- **No skills:** clicking a monster sends Move only. Neither the player nor monsters have skills yet.

## Lessons

- Static analysis gave correct per-component field layouts but the **wrong component order and set**. Trace the live
  object (Frida) before trusting an order.
- First-chance exceptions are visible with `Process.setExceptionHandler`. The client's own handlers turn some of them
  into dialogs ("Buffer Underflow") and others into crashes later (the null call in run 5).
- UI code assumes server state exists (inventories by row, passive slots). Each null crash points at the next message to
  send; scanning all call sites of the lookup (`0x141d621a0`) beats fixing them one by one.
- A world object that exists but neither draws nor animates is probably outside the wake zone (0x1a9).
- Carrier fields can belong to a hidden channel (HNC): an innocent zero can be a protocol violation.
- Each round costs a client restart (`live.ps1`), a login and Play. Window positions change between launches, so take
  a screenshot before clicking.
