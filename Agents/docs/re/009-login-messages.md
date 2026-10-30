# 009 — Login connection message set (static, 2026-10-16)

Client 3.29.3b (`Client/Game.exe`, image base 0x140000000). Derived statically from the Ghidra project
(`scripts/re/ghidra/g.sh` with `Decomp.java`, `RefsIn.java` and the new `FindSenders.java`). No traffic was captured, so
none of this has been checked against a live server yet. The implementation is in
`src/GameServer.Core/Protocol/Login/LoginWire.cs` and `LoginMessages.cs`, with tests in
`tests/GameServer.Core.Tests/LoginMessageTests.cs`.

## Framing
- After the two plaintext `0x0002` messages (doc 008), each direction is one Salsa20 stream of messages sent back to back.
- A message is `u16 id` (big-endian) followed by its fields. There is **no length prefix and no terminator**. The client
  flushes after each message (`0x140dfb8c0`), but TCP can merge or split messages, so the reader has to know every
  layout.
- Receive side: the factory `0x14219c350` maps the id to a class, then calls vtable slot 2 (read). The reader
  `0x140dfb630` sets the stream flag `+0x252` when it runs out of bytes, and the message is retried after more data
  arrives. An unknown id hits the factory's default case, which creates no object. `LoginCodec.TryDecode` behaves the
  same way: it returns false when it needs more data and throws on an unknown id.
- Message class vtable layout: `[0]` destructor, `[1]` write (id + fields), `[2]` read (fields only), `[3]` clone.
  The S→C dispatch is the switch in `0x14213f190` on `msg+8` (the id). Fields start at `msg+0x10`.

## Field encodings
| Name | Wire | Client code |
|---|---|---|
| u8 / bool | 1 byte (bools read as `!= 0`) | `0x140dfb630` / `0x140dfb760` |
| u16 / u32 | big-endian (`htons`/`htonl`) | |
| raw32 / raw64 | memory copied without swapping, so **little-endian** | e.g. `0x14219bd20`, `0x1424ed0d0` |
| bytes[n] | fixed n bytes | |
| blob | u16 length + bytes | read `0x1408402a0` / `0x141fa4610`, write `0x141fa44f0` |
| str | blob holding a narrow `std::string` (UTF-8) | `0x1408402a0` |
| wstr | u16 **code-point** count, then per code point: `< 0x80` as 1 byte; `0x80` + UTF-16 unit LE; `0xC0` + code point as u32 LE. Lone surrogates are sent as `0x80 FFFD`. Any other tag, or an `0x80` unit in D800–DFFF, is a "Protocol violation reading wstring from the network". A high surrogate sent with `0xC0` is paired with a following `0xC0` low surrogate (which counts as two elements). | write `0x140e0c9b0`, read `0x140e0cbe0` |
| vec8&lt;T&gt; / vec32&lt;T&gt; | u8 or u32 (BE) count, then the elements | `0x142507200`, `0x1424ed0d0` |

`wstr` also has a raw mode (u16 count + UTF-16 units) selected by socket byte `+0x254`. The socket constructor
(`0x140dfae09`, `0x140dfb100`) clears it, and the only other writer (`0x1410736e0`) belongs to a different class, so the
login connection uses the compact form.

Realm byte: many C→S messages send `DAT_143a6002c | 0x40`. `0x1422ee1b0` sets `DAT_143a6002c` to 0 (international),
6 or 9 (regional builds).

## Server → client (factory `0x14219c350`)
| Id | Vtable | write / read | Fields (wire order) | Client handling (`0x14213f190`) |
|---|---|---|---|---|
| 0x01 | 0x14316c930 | 0x140912f40 / (none) | — | no dispatch case |
| 0x02 | 0x1435c0038 | 0x141f9d710 / 0x141f9d7f0 | blob key, blob signature | key exchange (doc 008) |
| 0x04 | 0x1435dbac8 | 0x14219b140 / 0x14219b230 | u16 result, wstr name, bytes[32], u8 flags0, u8 flags1, blob data | result 0 → state 3. Flags0 bits 0–4 → +0xe50..e54, bit5 → +0xea8, bit6 → +0xea9, bit7 → +0xe55. Flags1 bit0 → +0xe57. Data is copied to +0xe30. Listeners at +0x910 receive (result, digest, name, data). |
| 0x0b, 0x0d, 0x0f | 0x1435bf7e8 | 0x141f92870 / 0x141f928d0 | u16 | listeners +0x990 / +0xa90 / +0xb10 |
| 0x11 | 0x1435dba88 | 0x14219b5a0 / 0x14219b610 | u8 kind, blob | blob parsed into 17×0x80 records (`0x14213d060`), stored via `0x142141000(+0xc10, kind)` |
| 0x13 | 0x1435dbae8 | 0x14219b850 / 0x14219b8d0 | u16, bool | listeners +0xa10. If the bool is set, posts to `/api/tracking-conversions/` |
| 0x14 | 0x1435dba08 | 0x14219b9c0 / 0x14219bb20 | str host, u16, u32, u16 area hash, u32, bytes[64], bytes[8], u8 | instance assignment: area hash lookup ("Area reported by instance does not exist"), listeners +0x290 |
| 0x15 | 0x1435dba48 | 0x14219b3f0 / 0x14219b490 | vec8&lt;Entry15&gt;, u8, u8, blob | list → +0xe68, bytes → +0xe80/+0xe81, blob parsed like 0x11. Listeners +0x110 |
| 0x17 | 0x1435bf7e8 | as 0x0b | u16 reason | `0x14213e510`: disconnect, listeners +0x90 |
| 0x1a | 0x1435db9e8 | 0x14219b6d0 / 0x14219b740 | u16, vec32&lt;CharEntry&gt; | listeners +0x810 |
| 0x1b | 0x1435dba28 | 0x14219bd20 / 0x14219bdb0 | raw64 server time, vec32&lt;CharEntry&gt;, u8 flags (bit0 → +0x30, bit1 = append) | time offset = value − `_time64`. List → +0xe88. Count of entries with byte +0xe2 bit0 → +0xe58 |
| 0x1e | 0x1435bf7e8 | as 0x0b | u16 | listeners +0x890 |
| 0x1f, 0x20 | 0x14316c420 | 0x140912ff0 / 0x140913050 | u32 | listeners +0x710 / +0x790 |
| 0x24, 0x25, 0x26 | 0x1435bf650 | 0x141f91850 / 0x141f918b0 | wstr | `0x142141100` on +0x410 / +0x390 / +0x490 |
| 0x28, 0x29 | 0x1435bf650 | as 0x24 | wstr | no case seen in the decompiled part of the switch |
| 0x3c | 0x1435bf8a8 | 0x141f926a0 / 0x141f92720 | u32, u32 | second → +0xe60 |
| 0x3e | 0x1435dba68 | 0x141f95fa0 / 0x14219bef0 | u16, bool | |
| 0x41 | 0x1435c01b8 | 0x141f9f890 / 0x141f9f920 | str | |
| 0x43 | 0x1435bf630 | 0x141f9f430 / 0x141f9f4b0 | u8, wstr | |
| 0x44 | 0x1435bf7e8 | as 0x0b | u16 | |
| 0x47 | 0x1435dbaa8 | 0x14219bfe0 / 0x14219c1c0 | u32, u16, vec8&lt;str&gt; | |

`Entry15` (0x50-byte struct): wstr +0x00, wstr +0x20, u8 +0x49, u8 +0x4d, u8 +0x48, u32 +0x40, u8 +0x4a, u16 +0x44,
u16 +0x46, u8 +0x4b, u8 +0x4c. The client keeps the maximum of +0x48 in +0xeac.

`CharEntry` (0xe8-byte struct, probably a character): wstr +0x10, wstr +0x40, wstr +0x60, wstr +0x80, wstr +0xa0,
raw64 +0xd0, raw64 +0xd8, raw64 +0xc8, bytes[3] +0xe0..e2, u8 +0xc0, u8 +0xc1, raw32 +0xc4 (default 0xd1).

## Client → server (no factory in the client; found from writes to the login socket `login+0xdd0`)
`FindSenders.java 0xdd0` lists every function that uses displacement 0xdd0. The login object is `*(global+0x358)`
(`0x1400f3530`).

| Id | Class / writer | Sender | Fields (wire order) |
|---|---|---|---|
| 0x01 | inline | 0x14213e210 | — (keepalive, every 10 s; that function's `this` is the login object's +8 base, so it reaches the socket as `+0xdc8`) |
| 0x02 | 0x1435c0038 | 0x14213e610 | blob key, blob signature (empty) |
| 0x03 | 0x1435d8b88 / 0x14213c660 (read 0x14213c7c0) | 0x14213ea10, 0x140d05f70 | u32 version (2), str account, bytes[32] machine id A (`0x14213e970`), bytes[32] password digest, bytes[32] machine id B (`0x14299b130`), bool, bool, u8 realm\|0x40, u8 |
| 0x05 | 0x1435d8b30 / 0x14213c1c0 | 0x14213eda0 | u8 realm\|0x40, bytes[32] id B, bytes[32] id A, u8, blob token |
| 0x06, 0x07 | 0x1435d8ae8 / 0x14213bf60 | 0x14213eba0, 0x14213ef90 | str ticket, u8 realm\|0x40, bytes[32] id B, bytes[32] id A, u8 |
| 0x08 | inline | 0x140d60410 | u32, raw64. Sent through `*(global+0x350)+0xdd0`, which is **not** the login object, so this may belong to another connection |
| 0x0a | 0x1435d8aa0 / 0x14213c9f0 | 0x140cf3150 | bytes[32], bytes[32]: SHA-256 (`0x140e23290`, IV checked) of two UTF-16LE strings |
| 0x0c | 0x1435bfcd8 / 0x141f950c0 | 0x140cfafa0 | wstr, u8 |
| 0x0e | 0x1435c0898 / 0x141f92c90 | 0x1421406f0 | u8 realm, u32 |
| 0x12 | 0x1435d8b10 / 0x14213cb50 | 0x142140490 | wstr, wstr, u32, u32, wstr, bytes[32] (SHA-256 of a UTF-16LE string, or zeros) |
| 0x18, 0x19, 0x21, 0x23, 0x39 | inline | 0x140d13060, 0x140d166a0, 0x140d285d0, 0x140d268b0, 0x140d264b0 | — |
| 0x1c | inline | 0x142140a80 | u16, u16 (defaults to +0xe58) |
| 0x22 | 0x1435d8ac8 / 0x14213ce00 | 0x142140b40 | bytes[5], parsed from a 10-hex-digit string |
| 0x2a, 0x2b | 0x1435bfcd8 (inline) | 0x140d26780, 0x140d26650 | wstr, u8 realm\|0x40 |
| 0x2c | 0x1435d8a80 / 0x14213c4e0 | 0x140d01a60 | wstr, u8 realm\|0x40, blob token (Epic EOS auth token) |
| 0x3a | inline | 0x140d08c80, 0x140d08de0 | u8 |
| 0x3d | 0x1435d8b68 / 0x14213cee0 | 0x142140890 | wstr, wstr, bool |
| 0x3f, 0x40 | 0x1435bf650 (inline) | 0x142140d70, 0x142140e60 | wstr |
| 0x42 | inline | 0x142140810 | u8 |
| 0x45 | inline (class 0x1435c01b8's layout) | 0x1400f50f0 | blob string. Sent when not in an instance; in an instance the same function sends instance 0x2fa with the string |
| 0x46 | inline | 0x140d14690 | u32 (0) |

The account login (0x03) is written field by field. MachineIdA (struct +0x54) comes **before** the password digest
(+0x34) on the wire.

## Limits / next steps
- The C→S list is complete: every caller of the stream flush `0x140dfb8c0` was accounted for (generated list with
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
  `0x140d05f70` reads the password widget (`+0x388`), hashes `length*2` bytes with `0x140e23290`, and reads the account
  name from `+0x398` (converted to UTF-8 by `0x140e359a0`). The digest stands in for the password, so the server must
  keep it out of logs.
- 0x04 `Result`: the client only checks for 0, which moves it to state 3. Other values go to UI listeners (`+0x910`).
  The mapping onto `BackendErrors` (row 4 `AccountDoesNotExist`, row 5 `InvalidPassword`) is not verified. The server
  sends 5 on failure.
- `GameServer.Auth` answers 0x03 through `LoginServerSession` against the dev account store. Its Python smoke test
  logged in with an encrypted 0x03 and got 0 for the right password and 5 for a wrong one. A real client has not been
  tried yet.
