# 03 — Area and world: 0x1d, 0x7d, 0x22, 0x21, world activation

Code: `AreaInstance`, `InstanceProtocol.EnterArea` (`Protocol/Instance/InstanceProtocol.cs`),
`InstanceEntities.SetLocalPlayer` (`Protocol/Instance/InstanceEntities.cs`). Tests: `InstanceProtocolTests`,
`InstanceEntitiesTests`.

## 1. S→C 0x1d: the instance's area

Dispatcher case 6. Class `0x1435da180` (0x168 bytes, ctor `0x14219d940`), write `0x1421415c0`, read `0x142141ef0`.

### Wire layout (write order)

| # | Field | Object offset | Notes |
|---|---|---|---|
| 1 | u32 | +0x10 | instance id ("Client-Safe Instance ID = N", stored at instance `+0xc280`) |
| 2 | u16 | +0x14 | **area hash** (WorldAreas.HASH16). Unknown → "Instance sent world area that does not exist with hash: N" (fatal) |
| 3 | wstr | +0x30 | → `0x141fe63f0` → `+0xc3b0` |
| 4 | u32 | +0x18 | **seed** |
| 5 | u8 | +0x1e | ctor default 3 → `+0xc408` |
| 6 | u8 | +0x20 | default 0 → `+0xcf30` |
| 7 | u8 | +0x1f | default 2 → `+0xc409` |
| 8 | u8 | +0x24 | default 0x23 → `+0xcf44` |
| 9 | 3 raw bytes | +0x21..+0x23 | flag bits (see below) |
| 10 | u8 | +0x1c | **level**. 0 = the area's own level (`0x140aa9900`), clamped to 1..100 |
| 11 | u8 | +0x1d | |
| 12 | u8 | +0x28 | default 0x3c → `0x142142c00` |
| 13 | u16 | +0x16 | **only if** flag 0x21 bit 3: second area hash |
| 14 | vec16⟨u32⟩ | +0x68 | `0x141fa3a70` |
| 15 | vec16⟨u8⟩ | +0x80 | stored as 4-byte elements, sent as 1 byte each |
| 16 | vec8⟨u8⟩ | +0x50 | visible AchievementSetsDisplay ids → instance +0xc418 (dispatcher r13 +0xc410); Delve sends permanent set 2 to enable the shared upgrade window |
| 17 | vec16⟨u16⟩ | +0xe0 | |
| 18 | vec8⟨u8⟩ | +0x98 | only if flag 0x21 bit 7 |
| 19 | vec8⟨u8⟩ | +0xb0 | only if flag 0x22 bit 0 |
| 20 | `0x1421732e0` | +0xc8 | only if flag 0x22 bit 1 |
| 21 | varint pair lists | +0x100/+0x120/+0x138 | only if flag 0x21 bit 6 |
| 22 | vec16⟨u8⟩ | +0x150 | only if flag 0x22 bit 2 |

Flag bits read by the handler (from the 3 bytes at +0x21): 0, 1, 2, 4, 5 (inverted) → `+0xc3e5..` bytes; 0xc, 0xd, 0xe →
world setters; 3 = second area hash; 2 → `0x142142d30`; 0xa → `0x142142c90(+0x150)`; 0xf → with the area's own flag.

`AreaInstance.Encode()` defaults to the minimal form, with optional league stat lists and extra terrain data.
`TerrainData` sets flag 0x22 bit 2 and appends a big-endian u16 byte count followed by the extra-data bytes.
The extra-data decoder (`0x141ec7220`) has its own two-byte flags and little-endian nested records.
Its first flag byte's bit 5 selects the Delve terrain block (`0x141ec5f20`).
See [Delve](21-hideouts.md#delve-sulphite-and-the-crawler-live-2026-10-29) for the initial mine control.

### What the client does

Sets the area and seed, logs, and queues area generation (`0x142155310` → job `0x140e31310`). Live log:
```
Client-Safe Instance ID = 1
Generating level 13 area "1_1_town" with seed 1
Tile hash: 1139181677
Doodad hash: 2393431363
[SCENE] Set Source [Lioneye's Watch]
[SCENE] Static Walkability Texture: 230 x 138      (one third of the 690 x 414 grid, see §7)
```
An old real-server session in `Client/logs/Client.txt` (2026-02-13, Highgate) has the same order, followed by
"Joined guild …" and ": You have entered Highgate.".

## 2. C→S 0x7d: area generated

When generation finishes, the tick (`0x142148880`, flag `+0xe1f9`) sends **0x7d** with two u32 from world `+0x114`:
the **tile hash** and the **doodad hash** that were just logged. Live: `007D 43E6886D 8EA8DD43` (0x43E6886D =
1139181677, 0x8EA8DD43 = 2393431363). The Gateway sends the player and enters the area on this message.

## 3. S→C 0x22: local player id

Dispatcher case 0xb. Class `0x14316c420` (u32). `0x142186fa0(world, id)` stores it at **`world+0x980`** and the
dispatcher copies it to instance `+0xc804`. We send `0x22 0x00001000`.

## 4. World messages are queued until the world is active

The world group's handler `0x142187480` returns at once unless `world+0xb7a` is set. While the world is inactive,
world messages (0x2b9 among them) go into a list at `world+0x9e0`. `0x142183670` (world vtable +0x1c8) is the
activation:

1. `world+0x968` = the caller (the instance client), then `0x142189120` and `0x141ebf050`
2. **sets `+0xb7a`, replays every queued message through `0x142187480`, frees them, clears `+0xb7a`**
3. takes the local player `world+0x970` (set while replaying 0x2b9, see [04](04-player-entity.md)) and looks up its
   **"Player" component without a null check**, then registers listeners on it

So 0x22 + 0x2b9 without 0x21 is harmless: the client only queues them, which was verified live (no reaction). **0x21 is
what triggers parsing the player.** Sending 0x21 without a valid player object crashes the client.

Other writers of `+0xb7a`: `0x142183ce0` (sets it, at `0x142183d20`) and a clear at `0x142184579`.

## 5. S→C 0x21: enter area

Dispatcher case 10. Class `0x1435dcb80` (0x80 bytes, ctor `0x1421a2d40`), write `0x1421a2da0`, read `0x1421a2fe0`.

Targets after "→" are relative to the dispatcher's `this` (= instance client + 8).

| # | Field | Object offset |
|---|---|---|
| 1 | vec16⟨Record68⟩ | +0x10 → instance `+0xc490` |
| 2 | vec16⟨Record68⟩ | +0x28 → instance `+0xc4c0` |
| 3 | vec16⟨(u32, u32)⟩ | +0x40 → `0x140df9720(+0xc5b0)` |
| 4 | wstr | +0x58 → `+0xcd08` |
| 5 | 3 raw bytes | +0x78: 24 flag bits (0, 1, 2, 3, 4, 5, 9, 0xa, 0xb, 0x12, 0x14, 0x16 and more are read individually) |
| 6 | u8 | +0x7e → world vtable +0x150 |
| 7 | u8 bits | bit0 → +0x7b, bit1 → +0x7c, bit2 → +0x7d |

**Record68** (0x68 bytes, write `0x141d4d020`, probably a player/party entry): u32 +0x28, u32 +0x2c, wstr +0x08,
blob16 +0x48, u16 +0x38, 2 raw bytes +0x3d/+0x3e (flags), u16 +0x3a, u8 +0x30, u8 +0x31, u8 +0x32, u8 +0x34; then if
flags(+0x3d) & 0x20 raw u64 +0x00, & 0x10 u8 +0x3c, & 0x40 4 raw bytes +0x3f.

We send the minimal form: `0021 0000 0000 0000 0000 000000 00 00` (`InstanceProtocol.EnterArea`).

### The 0x21 handler

Sets many instance flags from the 24 bits, copies the vectors, then calls **`0x142156d80`**:

1. world vtable +0x1c8 → **world activation** (section 4)
2. `0x1400f24b0(world)` = the first local entity; `0x14013f1e0` = its "Player" component → instance `+0xc810`
3. class/ascendancy from the player's byte `+0x158`: `bVar2 & 0x1f` = Characters.dat row, `(bVar2>>5&1)<<2 | bVar2>>6`
   = ascendancy, 7 = none
4. binds the player to the instance, sets **state 3**, notifies listeners (`+0x978`), which builds the in-game UI (the
   UI needs inventories and passive slots: [05](05-inventories-and-ui.md))

## 6. Spatial index (where a bad position crashes)

Positioned objects are bucketed by 23×23-grid-unit tiles: `0x141d83fb0` computes `(x/23, y/23)` and inserts into
`buckets[ty * width + tx]` (0x38-byte buckets at `+0x28`, width at `+0x18`) with `0x141d7c4c0`, with **no bounds
check**. A garbage or out-of-area position becomes a wild write. This was the first crash in
[06](06-debugging-log.md).

## 7. Area size, walkability and the spawn position

Positions are in grid units of the area's terrain grid. The log line "Static Walkability Texture: 230 x 138" is a
**one-third** downsample: `0x1410ddb90` builds it as `(w+2)/3 × (h+2)/3` from a terrain provider (`param_2`, vtable
`+0x50` returns `{i32 w, i32 h, vector a, vector b}`). For Lioneye's Watch (1_1_town, seed 1) the grid is **690 × 414**
(30 × 18 tiles of 23).

`scripts/re/frida/dump-walkability.js` hooks `0x1410ddcfd` (right after that call) and writes both vectors (1 byte per
cell) to `Tools/re/out/walk-{a,b}.bin`. Layer **a** is a distance-from-edge field: 5 inside (13 039 cells, bbox x 124–387,
y 121–309), falling to 1 at the boundary, 0 = blocked. The client walks on 2 and 3; the movement estimator treats ≥ 2 as
walkable ([07](07-latency-visibility-movement.md) §5). Layer b is mostly 5 outside the town (probably a different mask, unused).
Picture: [../re/evidence/1_1_town-walkability.png](../re/evidence/1_1_town-walkability.png).

The Gateway spawns at **(197, 261)**, the walkable cell farthest from any edge (clearance 32). The old (115, 69) was sea.
Result: [../re/evidence/2026-10-16-spawn-on-land.jpg](../re/evidence/2026-10-16-spawn-on-land.jpg) (beach, pier,
minimap marker inside the town outline). The character model is not visible yet.

## Open

- The real player start position for each town (area data, or a real capture).
- What the 0x21 record lists and flag bits mean (party, league, PvP …).
- The remaining 0x1d fields and flags.
