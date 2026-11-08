# 009 — Login connection message set (static, 2026-10-16)

Client 3.29.3c (`Client/Game.exe`, image base 0x140000000; derived on 3.29.3b, addresses ported). Derived statically
from the Ghidra project (`scripts/re/ghidra/g.sh` with `Decomp.java`, `RefsIn.java` and the new `FindSenders.java`). No
traffic was captured, so none of this has been checked against a live server yet. The implementation is in
`src/GameServer.Core/Protocol/Login/LoginWire.cs` and `LoginMessages.cs`, with tests in
`tests/GameServer.Core.Tests/LoginMessageTests.cs`.

## Framing
- After the two plaintext `0x0002` messages (doc 008), each direction is one Salsa20 stream of messages sent back to back.
- A message is `u16 id` (big-endian) followed by its fields. There is **no length prefix and no terminator**. The client
  flushes after each message (`0x140dfa160`), but TCP can merge or split messages, so the reader has to know every
  layout.
- Receive side: the factory `0x1421a2cc0` maps the id to a class, then calls vtable slot 2 (read). The reader
  `0x140df9ed0` sets the stream flag `+0x252` when it runs out of bytes, and the message is retried after more data
  arrives. An unknown id hits the factory's default case, which creates no object. `LoginCodec.TryDecode` behaves the
  same way: it returns false when it needs more data and throws on an unknown id.
- Message class vtable layout: `[0]` destructor, `[1]` write (id + fields), `[2]` read (fields only), `[3]` clone.
  The S→C dispatch is the switch in `0x142145ae0` on `msg+8` (the id). Fields start at `msg+0x10`.

## Field encodings
| Name | Wire | Client code |
|---|---|---|
| u8 / bool | 1 byte (bools read as `!= 0`) | `0x140df9ed0` / `0x140dfa000` |
| u16 / u32 | big-endian (`htons`/`htonl`) | |
| raw32 / raw64 | memory copied without swapping, so **little-endian** | e.g. `0x1421a2690`, `0x1424f2c50` |
| bytes[n] | fixed n bytes | |
| blob | u16 length + bytes | read `0x14083f460` / `0x141fab040`, write `0x141faaf20` |
| str | blob holding a narrow `std::string` (UTF-8) | `0x14083f460` |
| wstr | u16 **code-point** count, then per code point: `< 0x80` as 1 byte; `0x80` + UTF-16 unit LE; `0xC0` + code point as u32 LE. Lone surrogates are sent as `0x80 FFFD`. Any other tag, or an `0x80` unit in D800–DFFF, is a "Protocol violation reading wstring from the network". A high surrogate sent with `0xC0` is paired with a following `0xC0` low surrogate (which counts as two elements). | write `0x140e0b250`, read `0x140e0b480` |
| vec8&lt;T&gt; / vec32&lt;T&gt; | u8 or u32 (BE) count, then the elements | `0x14250ddb0`, `0x1424f2c50` |

`wstr` also has a raw mode (u16 count + UTF-16 units) selected by socket byte `+0x254`. The socket constructor
(`0x140df96a9`, `0x140df99a0`) clears it, and the only other writer (`0x141075aa0`) belongs to a different class, so the
login connection uses the compact form.

Realm byte: many C→S messages send `DAT_143a6b89c | 0x40`. `0x1422f4b40` sets `DAT_143a6b89c` to 0 (international),
6 or 9 (regional builds).

## Server → client (factory `0x1421a2cc0`)
| Id | Vtable | write / read | Fields (wire order) | Client handling (`0x142145ae0`) |
|---|---|---|---|---|
| 0x01 | 0x1431768d8 | 0x140911160 / (none) | — | no dispatch case |
| 0x02 | 0x1435caa70 | 0x141fa4140 / 0x141fa4220 | blob key, blob signature | key exchange (doc 008) |
| 0x04 | 0x1435e6568 | 0x1421a1ab0 / 0x1421a1ba0 | u16 result, wstr name, bytes[32], u8 flags0, u8 flags1, blob data | result 0 → state 3. Flags0 bits 0–4 → +0xe50..e54, bit5 → +0xea8, bit6 → +0xea9, bit7 → +0xe55. Flags1 bit0 → +0xe57. Data is copied to +0xe30. Listeners at +0x910 receive (result, digest, name, data). |
| 0x0b, 0x0d, 0x0f | 0x1435ca200 | 0x141f99160 / 0x141f991c0 | u16 | listeners +0x990 / +0xa90 / +0xb10 |
| 0x11 | 0x1435e6528 | 0x1421a1f10 / 0x1421a1f80 | u8 kind, blob | blob parsed into 17×0x80 records (`0x1421439b0`), stored via `0x142147950(+0xc10, kind)` |
| 0x13 | 0x1435e6588 | 0x1421a21c0 / 0x1421a2240 | u16, bool | listeners +0xa10. If the bool is set, posts to `/api/tracking-conversions/` |
| 0x14 | 0x1435e64a8 | 0x1421a2330 / 0x1421a2490 | str host, u16, u32, u16 area hash, u32, bytes[64], bytes[8], u8 | instance assignment: area hash lookup ("Area reported by instance does not exist"), listeners +0x290 |
| 0x15 | 0x1435e64e8 | 0x1421a1d60 / 0x1421a1e00 | vec8&lt;Entry15&gt;, u8, u8, blob | list → +0xe68, bytes → +0xe80/+0xe81, blob parsed like 0x11. Listeners +0x110 |
| 0x17 | 0x1435ca200 | as 0x0b | u16 reason | `0x142144e60`: disconnect, listeners +0x90 |
| 0x1a | 0x1435e6488 | 0x1421a2040 / 0x1421a20b0 | u16, vec32&lt;CharEntry&gt; | listeners +0x810 |
| 0x1b | 0x1435e64c8 | 0x1421a2690 / 0x1421a2720 | raw64 server time, vec32&lt;CharEntry&gt;, u8 flags (bit0 → +0x30, bit1 = append) | time offset = value − `_time64`. List → +0xe88. Count of entries with byte +0xe2 bit0 → +0xe58 |
| 0x1e | 0x1435ca200 | as 0x0b | u16 | listeners +0x890 |
| 0x1f, 0x20 | 0x1431767e8 | 0x140911210 / 0x140911270 | u32 | listeners +0x710 / +0x790 |
| 0x24, 0x25, 0x26 | 0x1435ca068 | 0x141f98140 / 0x141f981a0 | wstr | `0x142147a50` on +0x410 / +0x390 / +0x490 |
| 0x28, 0x29 | 0x1435ca068 | as 0x24 | wstr | no case seen in the decompiled part of the switch |
| 0x3c | 0x1435ca2c0 | 0x141f98f90 / 0x141f99010 | u32, u32 | second → +0xe60 |
| 0x3e | 0x1435e6508 | 0x141f9c9d0 / 0x1421a2860 | u16, bool | |
| 0x41 | 0x1435cabf0 | 0x141fa62c0 / 0x141fa6350 | str | |
| 0x43 | 0x1435ca048 | 0x141fa5e60 / 0x141fa5ee0 | u8, wstr | |
| 0x44 | 0x1435ca200 | as 0x0b | u16 | |
| 0x47 | 0x1435e6548 | 0x1421a2950 / 0x1421a2b30 | u32, u16, vec8&lt;str&gt; | |

`Entry15` (0x50-byte struct): wstr +0x00, wstr +0x20, u8 +0x49, u8 +0x4d, u8 +0x48, u32 +0x40, u8 +0x4a, u16 +0x44,
u16 +0x46, u8 +0x4b, u8 +0x4c. The client keeps the maximum of +0x48 in +0xeac.

`CharEntry` (0xe8-byte struct, probably a character): wstr +0x10, wstr +0x40, wstr +0x60, wstr +0x80, wstr +0xa0,
raw64 +0xd0, raw64 +0xd8, raw64 +0xc8, bytes[3] +0xe0..e2, u8 +0xc0, u8 +0xc1, raw32 +0xc4 (default 0xd1).

## Client → server (no factory in the client; found from writes to the login socket `login+0xdd0`)
`FindSenders.java 0xdd0` lists every function that uses displacement 0xdd0. The login object is `*(global+0x358)`
(`0x1400f37c0`).

| Id | Class / writer | Sender | Fields (wire order) |
|---|---|---|---|
| 0x01 | inline | 0x142144b60 | — (keepalive, every 10 s; that function's `this` is the login object's +8 base, so it reaches the socket as `+0xdc8`) |
| 0x02 | 0x1435caa70 | 0x142144f60 | blob key, blob signature (empty) |
| 0x03 | 0x1435e3658 / 0x142142fb0 (read 0x142143110) | 0x142145360, 0x140d060a0 | u32 version (2), str account, bytes[32] machine id A (`0x1421452c0`), bytes[32] password digest, bytes[32] machine id B (`0x1429a4490`), bool, bool, u8 realm\|0x40, u8 |
| 0x05 | 0x1435e3568 / 0x142142b10 | 0x1421456f0 | u8 realm\|0x40, bytes[32] id B, bytes[32] id A, u8, blob token |
| 0x06, 0x07 | 0x1435e3540 / 0x1421428b0 | 0x1421454f0, 0x1421458e0 | str ticket, u8 realm\|0x40, bytes[32] id B, bytes[32] id A, u8 |
| 0x08 | inline | 0x140d5db80 | u32, raw64. Sent through `*(global+0x350)+0xdd0`, which is **not** the login object, so this may belong to another connection |
| 0x0a | 0x1435e3630 / 0x142143340 | 0x140cf30c0 | bytes[32], bytes[32]: SHA-256 (`0x140e21b30`, IV checked) of two UTF-16LE strings |
| 0x0c | 0x1435ca710 / 0x141f9baf0 | 0x140cfaf60 | wstr, u8 |
| 0x0e | 0x1435cb2d0 / 0x141f99580 | 0x142147040 | u8 realm, u32 |
| 0x12 | 0x1435e35c0 / 0x1421434a0 | 0x142146de0 | wstr, wstr, u32, u32, wstr, bytes[32] (SHA-256 of a UTF-16LE string, or zeros) |
| 0x18, 0x19, 0x21, 0x23, 0x39 | inline | 0x140d13090, 0x140d166d0, 0x140d291c0, 0x140d27220, 0x140d26e20 | — |
| 0x1c | inline | 0x1421473d0 | u16, u16 (defaults to +0xe58) |
| 0x22 | 0x1435e3520 / 0x142143750 | 0x142147490 | bytes[5], parsed from a 10-hex-digit string |
| 0x2a, 0x2b | 0x1435ca710 (inline) | 0x140d270f0, 0x140d26fc0 | wstr, u8 realm\|0x40 |
| 0x2c | 0x1435e3610 / 0x142142e30 | 0x140d01b40 | wstr, u8 realm\|0x40, blob token (Epic EOS auth token) |
| 0x3a | inline | 0x140d08cb0, 0x140d08e10 | u8 |
| 0x3d | 0x1435e35a0 / 0x142143830 | 0x1421471e0 | wstr, wstr, bool |
| 0x3f, 0x40 | 0x1435ca068 (inline) | 0x1421476c0, 0x1421477b0 | wstr |
| 0x42 | inline | 0x142147160 | u8 |
| 0x45 | inline (class 0x1435cabf0's layout) | 0x1400f5380 | blob string. Sent when not in an instance; in an instance the same function sends instance 0x2fc with the string |
| 0x46 | inline | 0x140d146c0 | u32 (0) |

The account login (0x03) is written field by field. MachineIdA (struct +0x54) comes **before** the password digest
(+0x34) on the wire.

## Limits / next steps
- The C→S list is complete: every caller of the stream flush `0x140dfa160` was accounted for (generated list with
  the other connections: [../protocol/c2s.md](../protocol/c2s.md)). The S→C read sequences are in
  [../protocol/schema.md](../protocol/schema.md) (login group); the factory's 27 classes are exactly its switch cases.
- Open: 0x08 goes through `*(global+0x350)+0xdd0`, not the login object `*(global+0x358)`; which connection that is
  is not known.
- Field meanings beyond what the dispatch code shows are unknown, and the C# field names reflect that (`U16_30`,
  `B49`, …).
- Next: run a patched client against `GameServer.Auth serve` (it now logs decoded messages). The
  expected order is C→S 0x03 (or 0x05/0x06/0x07/0x2c), then S→C 0x04, then probably 0x1b.

## Account login semantics (2026-10-16)
- `PasswordDigest` in 0x03 is **unsalted SHA-256 of the password as UTF-16LE**, all zeros if the field is empty.
  `0x140d060a0` reads the password widget (`+0x388`), hashes `length*2` bytes with `0x140e21b30`, and reads the account
  name from `+0x398` (converted to UTF-8 by `0x140e37cc0`). The digest stands in for the password, so the server must
  keep it out of logs.
- 0x04 `Result`: the client only checks for 0, which moves it to state 3. Other values go to UI listeners (`+0x910`).
  The mapping onto `BackendErrors` (row 4 `AccountDoesNotExist`, row 5 `InvalidPassword`) is not verified. The server
  sends 5 on failure.
- `GameServer.Auth` answers 0x03 through `LoginServerSession` against the dev account store. Its Python smoke test
  logged in with an encrypted 0x03 and got 0 for the right password and 5 for a wrong one. A real client has not been
  tried yet.
