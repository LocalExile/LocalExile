# 28 — Player trade, known players and the social requests

Traced live 2026-10-28 with two clients in one town (3.29.3b). The player trade reuses the NPC sale's trade window and
offer messages ([17](17-npcs.md) "Sell"); this file covers what is specific to two players.

## 1. Client requests

| C→S | Bytes | Layout | Sent by |
|---|---|---|---|
| **0xba** | 6 | u32 BE the other player's object id (as the sender sees it: its `PeerId`) | "Trade" on a player |
| **0xb0** | 6 | u32 BE the requester's object id | Accept on the trade request notification |
| 0xb1 | 7 | u32 BE the other party, u8 | Decline on the notification, Cancel on the waiting box, Cancel in the window |
| **0x93** | var | wstr the character's name | "Invite to Party" |
| **0x95** | 6 | u32 BE the party id (from 0xa9) | Accept on the party invite notice |
| **0x96** | 6 | u32 BE the party id | "Leave Party" |
| **0x80** | var | wstr name, u8 list, u8 action | Friends: list 0; add 0, remove/decline 1, accept 2 |

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
- with bit 2 (or bit 5): wstr character (`+0x70`), u8 (`+0xb9`), u8 (`+0xb8`, the packed class); with bit 2 also u16 BE
  (`+0xa8`, sent as the level), u16 BE (`+0xac`), wstr (`+0x90`), u8 (`+0xb4`), then the pairs;
- without bit 2: u64 (`+0xa0`).

The bit 1 pair is the portrait frame (`+0xb0`) and the portrait (`+0xb2`); the client's writer leaves it out when both are
0 ([23](23-microtransactions.md) §3).

The Gateway sends `InstanceSocial.KnownPlayer` (flags 0x04, with the pair 0x06) for each side before the request.

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

Trade requires living, connected players in the same instance presence and full league, with matching modes.
Either SSF flag refuses the request and acceptance, including forged packets in a shared SSF town.
Offers and final acceptance recheck eligibility; completion holds both combat locks through one transaction
containing both inventory saves. Failed settlement leaves both durable inventories unchanged.

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

## 5. Party (live 2026-10-29, two clients in one town)

Invite, accept and leave work. The Gateway keeps the parties (`Program.Social.cs`: `PartyInvites`, `Parties`).

- **S→C 0xa9** (class `0x1435dc860`, read `0x1421a18b0`, dispatcher case 0x2a at `0x14214c53c`) has this layout:
  1. u32 party id (`+0x68`);
  2. wstr leader character (`+0x28`);
  3. wstr leader account (`+0x48`);
  4. u32 party id again;
  5. u8 `+0x6e`, the receiving player's own index in the member list (0xff none);
  6. u8 `+0x6c`, u8 `+0x6d`, u8 `+0x6f`;
  7. u8 count, then count × (wstr account name, u8 status).

  `0x142149d80` maps a status byte 0 → 1, 1 → 2, 2 → 0, any other → 3 (none).
- **The invite notice.** "{account} sent you a party invite" (ClientStrings `0x43f`) is built by `0x140aee190` through
  `0x140dd0ad0` on the event at `+0x7f78` (case 0x2a's slot). It shows only when all of these hold:
  - the party id is one the client doesn't know (`+0xc668`; a reused id only updates the entry);
  - the second wstr is an account name in the known players (`0x1421669a0` → `0x142166a50`), so both sides get 0xa7 first;
  - `+0x6c` is 0 and `+0x6d` is non-zero.

  The Gateway sends a fresh id per invite, `+0x6d` = 1, and both members with status 0.
- **Accept** is C→S 0x95. Accept and Decline call `0x142162ad0` and `0x142162a30` (C→S 0x80 with u16 0x200 / 0x100) only
  for a non-empty name; live, Accept sent 0x95. Decline is not answered yet.
- **The party.** Every member gets 0xa9 with its own `+0x6e`, the leader with status 2 and the others with 1, members by
  account name.
  - Every member, the player itself too, must be in the known players first.
  - Without its own entry, the joining client threw (a huge string copy in `0x14020b230`) from the party-changed event
    (`+0x7578`, raised by `0x142157450`).
- **Leaving.** C→S 0x96 is answered with **S→C 0xaa** (class `0x1435bf570`, read `0x141f9c0b0`, case 0x2b at
  `0x14214cb45`): u32 party id, u8 reason.
  - For the client's current party (`+0xc680`) it resets the party; for another id it drops it from `+0xc668`.
  - S→C 0xab (case 0x2c) only clears a pending invite (`+0xc730`) and did nothing for a party.
  - With fewer than two members left, every member gets 0xaa (disbanded); otherwise the others get the smaller party.

## 6. Friends

The Social module handles the ordinary context-menu and Friends-panel actions through C→S `0x80`.
The final bytes are independent list/action bytes (`0x141f94db0`), not a network-order ushort.
List 0 selects friends; action 0 adds, 1 removes or declines, and 2 accepts an incoming invitation.
Other lists/actions do not mutate friendships.
Names resolve to durable account identities through the account/character store; self and unknown targets are refused.
Accept requires an existing incoming invitation.

S→C `0xa7` supplies account and character metadata, followed by **`0xc9`**: u8 flags, wstr account, wstr note.
Dispatcher case `0x3c` (`0x14214e3da`) puts flag bits 0/1 into the account map, or removes its entry for bit 2.
The Friends panel (`0x140a3a340`, `0x140a3bd70`) uses bit 0 to distinguish accepted friends from incoming invitations,
and `0xa7`'s character-present bit to split online and offline rows.
The implementation sends flags 1 for the owner's added friend, 0 for the other account's incoming invitation,
and 4 to remove the relation.
Offline metadata is `0xa7`, the account, an empty string, flags 0 and a raw u64 timestamp (currently 0).
Do not substitute `0xa3`: a native probe produced an unrelated hideout-invitation notification.

`SqliteFriendStore` stores directed invitations/acknowledgements under account GUIDs in `account_friends`.
Adding back accepts the invitation; remove/decline transactionally clears both directions of that pair.
Duplicate adds do not create duplicate rows; failed transactions leave the previous relation intact.
The list is independent of character and league and is reconstructed on area entry.
The module registers online connections on entry and discovers existing peers for first use after a module reload.
Departure sends offline metadata; re-entry republishes the current character's metadata.

Native verification with two clients confirmed context-menu add, the name-entry form, incoming invitation display,
Accept, confirmed Unfriend removing the row on both clients, offline display, acceptance while the sender is offline,
and reconstruction after a new login (including presence updates while the players occupy different areas).
Automated checks cover persistence/reopen, rollback, duplicates, self/missing identities, pair deletion,
Unicode names, action-byte framing and relation/offline packet layouts.
Friend notes, ignore lists and account status editing remain separate features.

## Open

- Party: Decline, kicking and promoting members, a member's disconnect or area change, the party panel's other fields
  (`+0x6c`, `+0x6f`, the leader's character string), and parties across instances.
- Friends: notes, ignore lists and account status editing.
- The exact meaning of 0xa7's other fields (class, league, online state) and of the request option `0x19`.
- Trades across instances (the client only offers Trade on a player it sees).
