# 013. Atlas progress without Ghidra

How the 3.29.3b Atlas "map completed" state was found and what the server now sends. Protocol summary:
[21-hideouts.md](../protocol/21-hideouts.md) §4. This note is the method, so a later field (Voidstones, bonus
objectives, Atlas passive points) can use the same path.

The official Ghidra project is the usual source ([tooling.md](tooling.md) §1). It was not required here: the instance
schema, the dispatcher jump table and a Capstone walk of `Client/Game.exe` were enough.

## 1. Why this path is faster

Ghidra on `Game.exe` is a one-hour import and a 12–15 s decompile per function. Atlas progress only needed:

1. A short list of S→C messages the Gateway has never sent.
2. One live `/raw` probe per candidate that looks like a node set.
3. Targeted disassembly of the functions that *draw* a completed node, then the functions that *write* that byte.

That is hours, not a full client analysis. Use Ghidra when a function is too large to read as assembly (the instance
dispatcher is 45 KB) or when a call graph has to be printed; do not start there for a UI flag.

## 2. Filter candidates from the schema

`Agents/docs/protocol/schema.md` already has every instance S→C reader ([tooling.md](tooling.md) §7). Keep messages
that:

- are in the **instance** factory,
- have **never** appeared in `logs/live-gateway.log` (`=> 0xNNNN`),
- contain a **loop** (a set or list).

A one-off script over the schema and the live log produced 31 candidates. The useful ones for a node set were
short: a count or a section byte, then a loop of `raw2` / `raw4` or a fixed 22-byte blob. 0x1a3 and 0x1a4 sit in that
list (`schema.md` §0x1a3 / §0x1a4).

Do not send every candidate. Cross them with the instance dispatcher first (`scripts/re/dispatch_ids.py`): id → case
byte at `0x142154e14` → jump at `0x142154a1c + 4 * case`. A case that writes `r13 + 0xd030` / `+0xd070` is Atlas
state; one that writes `+0xd898` is not.

## 3. Live probe

`/raw <hex>` sends one S→C message as-is ([10](../protocol/10-chat-and-commands.md)). For a single Atlas node:

```
/raw 01a4 9a23 00 00 00
/raw 01a4 9a23 00 00 02
```

`9a23` is Chateau's AtlasNode HASH16 (big-endian). Flags `00` only add the node to the first set; `02` also adds it to
the completed set. Live 2026-11-01: the second command drew Chateau completed and opened Springs and Arachnid Nest.

A filled 0x1a3 or a 0x1a4 **after** S→C 0x21 (area enter, HUD up) is announced as a completion. Restore the saved set
with the 0x3d Atlas presets, **before** 0x21. A new completion in the instance is 0x1a4 plus one chat line.

## 4. Static walk of Game.exe (no Ghidra)

Local helpers (git-ignored `Tools/re/out/`, `pefile` + `capstone` in `.venv`):

| Need | How |
|---|---|
| Function bounds | PE `.pdata` unwind info, including chained unwind |
| Disassembly | Capstone, with RIP-relative string and call hints |
| Callers of a VA | cached xref index over the executable |
| Who uses `disp` | scan for the little-endian immediate, then confirm the instruction |

Recipe that found the completed flag:

1. **String → drawer.** `AtlasMapCompletedFrame` is texture-set entry 0 of getter `0x1407a1880`. The node drawer
   `0x14079e680` uses that frame when `byte [node+0x9ce] != 0`. Nearby state bytes: `+0x9ca`, `+0x9cb`, `+0x9cc`,
   `+0x9d1`, `+0x9d2`.
2. **Who writes `+0x9ce`.** Only `0x140786d90`. At `0x14078770d` it sets `+0x9cb` and `+0x9ce` from `al` of
   `0x14078fc90(progressObj = [rsi+0x468] + 0xd070, nodeEntry = r15)`, gated by vcall `[vt+0x378]` and
   `byte [[r15]+0xea]`.
3. **What `+0xd070` is.** `0x14078fc90` is an MSVC `unordered_set` lookup: hash `0x140284370`, bucket at
   `[rcx+0x18]`, compare `[node]+0x10`. Constructor `0x142144b50` builds three neighbour sets on the instance:
   `+0xcff0` (WorldAreas), `+0xd030` and `+0xd070` (AtlasNode rows). The dispatcher’s `r13` is the instance + 8, so
   its `+0xd068` is `+0xd070`.
4. **Who fills the sets.** Displacement `0xd070` / `0xd030` / `0xcfe8` in the instance dispatcher: case 0x7f
   (`0x142150ab1`, id **0x1a3**) and case 0x80 (`0x142150d5c`, id **0x1a4**).
5. **Wire layout from the reader, not the schema sketch alone.** Reader `0x1421a9970`: four `raw1` (`+0x71`, `+0x72`,
   `+0x70`, sections at `+0xa8`), then always `0x141fa4050` (u8 count of u16 pairs), then 22-byte masks when section
   bits 0x1 / 0x2 are set (`0x1421560b0`: bit `r` of byte `r / 8` is AtlasNode row `r`; rows from 170 ignored).
   Writer `0x1421a9750` writes the u16 id first, then those four bytes. 0x1a4 reader `0x141f96560`: u16 HASH16
   (byteswapped through `0x142f43e50`), three u8s.

`0x14076c2d0` / `0x14243b5b0` resolve an AtlasNode HASH16 through `Data/AtlasNode.dat`. That is the 0x1a4 key, not
WorldAreas HASH16 (that is C→S 0x00e7, node pick).

## 5. What the server does

`Modules/Core/World/Atlas/AtlasProgress.cs` loads AtlasNode rows that are on the Atlas (`IsNormalMap` or
`IsUniqueMap`, not `NotOnAtlas`, row &lt; 170). A map is completed when its WorldAreas boss, or any unique, dies in
its Area1/Area2. The AtlasNode Ids are saved per character (module data `completed`).

| When | Message | Why |
|---|---|---|
| Area enter, next to 0x3d | one filled 0x1a3 (`FullMessage`) | replace both masks before the HUD; no completion toast |
| First completion | 0x1a4 flags `0x02` and one `Tell` | event, not a restore |
| `/atlas_complete <one>` | 0x1a4 and one `Tell` | same as a first completion |
| `/atlas_complete all` | save only, one `Tell` | a filled 0x1a3 after 0x21 announced every map (live 2026-11-01) |
| `/atlas_reset` | empty 0x1a3 | clear the two sets |

`Game.ExtraPlayerMessages` is the early slot (`Player.cs`, after the 0x3d presets, before transitions and 0x21).
`OnEnterMessages` runs after 0x21 and must not restore Atlas progress.

## 6. Still open

The 0x1a3 bits 0x4 / 0x8 / 0x10, and bytes `+0x71` / `+0x72` / `+0x70`, are unread. They are the next place to look
for bonus objectives, Voidstones and Atlas passive points from completion. Same method: string or texture in the
Atlas UI → drawer flag → writer → displacement on the instance → dispatcher case → `/raw`.
