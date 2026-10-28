# 28 — Player trade, known players and the social requests

Traced live 2026-10-28 with two clients in one town (3.29.3b). The player trade reuses the NPC sale's trade window and
offer messages ([17](17-npcs.md) "Sell"); this file covers what is specific to two players.

## 1. Client requests

| C→S | Bytes | Layout | Sent by |
|---|---|---|---|
| **0xba** | 6 | u32 BE the other player's object id (as the sender sees it: its `PeerId`) | "Trade" on a player |
| **0xb0** | 6 | u32 BE the requester's object id | Accept on the trade request notification |
| 0xb1 | 7 | u32 BE the other party, u8 | Decline on the notification, Cancel on the waiting box, Cancel in the window |
| **0x93** | var | wstr the character's name | "Invite to Party" (read, not answered yet) |
| **0x80** | var | wstr the character's name, u16 (0 from the context menu) | "Add Friend" (read, not answered yet) |

The offer, accept and cancel messages of the open window are the sale's: C→S 0xb2 (drop / Ctrl + click, x = y = 0xff),
0xb3 and 0xb5 (Shift / Ctrl + Shift + click), 0xb6 (Accept: u32 the revision of the other side's offer inventory),
0xb7 (Cancel Accept), 0xb1 (Cancel).

## 2. Known players: S→C 0xa7

The client keeps a table of the players it knows (instance `+0xc738`, an FNV-hashed map keyed by the first wstr;
inserted by `0x142173b60` → `0x142178940`, dispatcher case 0x28). The trade request is looked up in it by character
name (`0x142166b40`, entry `+0x70`), the party list (0xa9, case 0x2a) looks its members up the same way, and an account
name lookup (`0x142166a50`, entry `+0x30`) exists. Both tables were empty with the server sending nothing, so the request
was dropped silently: S→C **0xa7** adds an entry, S→C 0xa8 (case 0x29) removes one by key.

0xa7 (class `0x1435dbda8`, 0xd0 bytes, write `0x1421a11a0` → `0x141f8f580`):

- wstr account (the key; entry `+0x30`), wstr (`+0x50`), u8 flags: bit 0 a u16 pair (`+0xaa`, `+0xae`) follows, bit 1 a
  u16 pair (`+0xb0`, `+0xb2`), bit 2 (`+0xba`) the character section, bits 3–6 `+0xbb` / `+0xbc` / `+0xbd` / `+0xbe`;
- with bit 2 (or bit 5): wstr character (`+0x70`), u8 (`+0xb9`), u8 (`+0xb8`); with bit 2 also u16 BE (`+0xa8`, sent as
  the level), u16 BE (`+0xac`), wstr (`+0x90`), u8 (`+0xb4`), then the pairs;
- without bit 2: u64 (`+0xa0`).

The Gateway sends `InstanceSocial.KnownPlayer` (flags 0x04) for each side before the request.

## 3. The request and the window: S→C 0xb8 flags

The in-game UI's 0xb8 listener (`0x140dccdf0`) reads struct `+0x50`:

| bit 0 | bit 1 | Shows |
|---|---|---|
| 0 | 1 | the notification "{name} sent you a trade request" (ClientStrings 0x441, `0x140afb820`), when the name is known and the option at `0x1419135f0(…, 0x19)` allows it |
| 1 | 0 | the requester's "Waiting for player to accept trade request..." box with Cancel (ClientStrings 0x2bd) |
| 1 | 1 | the trade window (`0x140c38d70` → `0x140c33e70`) |
| 0 | 0 | nothing |

Bit 0 is also the active trade for Ctrl + click (`0x140d62ec0`), 0x10 lets Accept send 0xb6 without hovering each item,
**0x04** greys the player's own offer grid and turns Accept into Cancel Accept (0xb7), **0x08** greys the other
player's offer grid (the window copies the struct at `0x140c32800`; `0x140c37140` sets each grid's `+0x33b` from
`+0x378` bits 2 and 3).

- Even the waiting box and the notification build the window's two offer grids (`0x140317440`, manager 1 `+0xca10`):
  0xb8 with offer inventory 0 crashed both clients at `0x1402d0a99`, so both offer inventories (0xc0, manager 1) exist
  before the first 0xb8.
- The window is created once per other party and found again by that id (`0x140c38d70`, a hash of struct `+0x08`):
  the 0xb8 that opens the window and every later state update keep the request's offer inventories. Creating new ones
  on accept left the grids bound to the old ones and every offered item invisible.
- The player trade uses the sale's offer inventory ids (`NpcShops.FirstTabId + 0x10` and `+ 0x11`); a player is never in
  both.

## 4. The Gateway (`Program.Social.cs`)

1. **0xba:** both sides get 0xa7 about the other and the two offer inventories; the requester gets 0xb8 flags 0x01
   (waiting), the other player 0xb8 flags 0x02 with the requester's name.
2. **0xb0:** the pending request becomes a `PlayerTrade`; both get 0xb8 flags 0x13 (the window).
3. **Offers:** an item is reserved in its owner's inventory (`PlayerInventories.Offer`, hidden from that client as in a
   sale), shown in the owner's offer (`mine`) and in the other's (`theirs`) under an id from the other player's id space.
   Taking it back restores it. Every change clears both acceptances.
4. **Accept:** 0xb6 counts only for the other offer's current revision; the state goes out as 0xb8 with 0x04 / 0x08.
   Both accepted: `PlayerInventories.CanExchange` checks both sides first, then `Exchange` swaps them in one save per
   side; 0xb9 (accepted) closes both windows. Not enough room clears the acceptances and says so.
5. **Cancel:** 0xb1, a disconnect or leaving the area (`CancelPlayerTrade`) releases both reservations first, restores
   both views and closes both windows. The waiting box's Cancel and the notification's decline close the request.

## Open

- Party (0x93) and friends (0x80): the answers, S→C 0xa9 party info and the friend list.
- The exact meaning of 0xa7's other fields (class, league, online state) and of the request option `0x19`.
- Trades across instances (the client only offers Trade on a player it sees).
