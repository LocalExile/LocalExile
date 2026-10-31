# Hinekora's Lock

Evidence for the Lock state, the preview request/response and the committed result, from the 3.29.3b client and a
native run on 2026-10-31. The item text is `ItemPopupHinekorasLockFunctionText` (ClientStrings 6976): "Hinekora's
Lock allows you to forsee the result of the next Currency Item used on this item. The Lock is removed when this item
is modified."

## State

- The foreseeing flag is the **Mods component's `u16` bit 8 (`0x0100`)**. The Mods reader `0x1421df6e0` stores it at
  item `+0x2c0`; the client then shows the purple glow and the Lock tooltip text.
- A non-zero `ForeseeSeed` is kept server-side and in the character save. The client does not need it: the preview and
  the committed result are computed by the server from `seed ^ currencyTypeHash` (`ItemRolls.ForeseeRandom`).
- Applying a second Lock is refused (`ItemErrorAlreadyHasHinekorasLock`, ClientStrings 6984). Gems, corrupted,
  mirrored, unmodifiable and already-foreseeing items are refused before the Lock is spent.
- Any successful modification clears the flag and the seed; a refused modification keeps them. Bench, Harvest and
  essence crafts clear the state through `ItemRolls.ClearForesee`.

## C→S 0x29c preview request

The client does **not** request the preview on hover. It sends an **unframed** `0x029c` when the player uses a
currency on a foreseeing item (right-click the currency to enter use mode, then left-click the item):

```
u16 0x029c
u16 key (BE, from the client counter at +0xe1fa)
u32 currency inventory (BE)
u32 currency item id (BE)
u32 target inventory (BE)
u32 target item id (BE)
```

Captured: `029C 0001 00000001 0000054D 00000001 0000054F` = key 1, Orb of Alchemy 1357 on Driftwood Wand 1359.
Sender `0x14216d5f0` (callers `0x1402cfcb8`, `0x1402cffea`), reader `0x141f9e8a0`. The sender only sends when both
inventories equal `client+0xc9d0` (the player inventory).

The client sends C→S `0x71` immediately after the `0x29c` on the same use: `0x29c` asks for the preview, `0x71` is
the commit.

## S→C 0x29f preview result

```
u16 0x029f
u16 keyA (BE) — the key from the 0x29c request
u16 keyB (raw LE) — the target item id
u64 count (raw LE)
count × item (u16 BE length + Item.Payload)
```

Handler `0x1421544c1`, reader `0x141fa1c50`, writer `0x141fa1b50`, class vtable `0x1435bfcf8`. The handler builds
`{u16 keyA, u16 keyB, vector<Item> at +8}` and dispatches it through `0x14019a240` to the instance-client event bus
at `+0x48f0` (`+0x48f8` is the listener field). The client logs `CurrencyApplicationForesightResult recieved`
(`0x1435d96a0`).

The key matters: a response with keyA = the target id instead of the request key is accepted and logged but the
client cannot match it to the pending use. The correct native response was
`029F 0001 4F05 0100000000000000 37 2F62B1D9...`: keyA 1, keyB 0x054F (1359), one item.

## Commit

The `0x71` handler checks the target's `Foreseeing` state. It uses the preview stored from the matching `0x29c`
(key, currency, target and seed all match), or recomputes the same seeded result when no preview was stored, clears
the flag and seed, consumes the currency and sends the normal S→C `0xbf`. The preview item and the committed item are
byte-for-byte the same payload.

Native run 2026-10-31:

- Lock 1353 on wand 1359 through `0x71`: the `0xbf` payload carried `Mods` flags `0x0100`; the client showed the
  Lock tooltip text.
- `0x29c` key 1, Orb of Alchemy 1357 on 1359: the server answered `0x29f` keyA 1, keyB 0x054F, count 1, with the
  preview item; the client logged `CurrencyApplicationForesightResult recieved`.
- `0x71` Orb of Alchemy on 1359: the `0xbf` payload's `Mods` flags were `0x0000`, the item was rare with the
  previewed mods, the Orb stack was spent and the save held `Foreseeing: false`, `ForeseeSeed: 0`.

## Open: native preview popup

The `0x29f` handler dispatches to the `+0x48f0` event bus, but the bus has **no listener** in every tested state
(hover with a currency, real `0x29c`/`0x29f`, item tooltip open, after the Lock). The client logs the message and
drops the event, so the preview popup does not render. The window that should register the listener is still to be
found; the likely candidates are `ItemPopupCreateInfo` / `ItemDisplayCaseWindow` (`ConnectEvents` lambda names at
`0x44b0c14` / `0x44b0d64`). The server-side preview/commit identity and all refusal/persistence rules are covered by
the Gateway tests.
