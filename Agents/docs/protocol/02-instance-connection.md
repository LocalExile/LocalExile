# 02 — Instance connection (tcp/6112): connect, opening, encryption, read loop

Implemented in `src/GameServer.Core/Protocol/Instance/InstanceProtocol.cs` and served by `GameServer.Gateway`. Tests:
`InstanceProtocolTests`. All of this is live-verified unless marked otherwise.

## 1. The instance client object

| What | Address / offset |
|---|---|
| constructor | `0x142144b50` (object 0xe208 bytes, created by `0x1415a4fe0`) |
| vtable at `this` | `0x14345e810`: slot 0 = tick `0x142148880` |
| vtable at `this+8` | `0x14345e8b8`: slot 0 = S→C dispatcher `0x14214a190` |
| login/info struct | `this+0xc298` (copy of the 0x14 fields, see [01](01-login.md#4-message-set)) |
| world object | `this+0xc278` (the dispatcher sees it at `+0xc270`) |
| stream (socket) | `this+0xc288` (dispatcher: `+0xc280`) |
| message pump | `this+0xc2b0` |
| own handler group | `this+0xc2a0`, vtable `0x1435d9d98` |
| **connection state** | `this+0xc3a0` (dispatcher: `+0xc398`): 1 idle or disconnected, 2 hello sent, 3 in area |
| player inventory manager | `this+0xc890` (see [05](05-inventories-and-ui.md)) |
| passive slots | `this+0xc2f8` (3 × 16 bytes), active index `this+0xc2f0` |
| local player id copy | `this+0xc804` (dispatcher: `+0xc7fc`) |

## 2. Connecting

1. The tick `0x142148880` starts a connect job when it has none: `0x142506420` → `0x1425064d0` (allocates the job and
   runs `CreateThread`) → thread `0x1425065b0` → `0x1425066e0`.
2. `0x1425066e0` is a happy-eyeballs connect: `getaddrinfo`, a non-blocking socket per address (`TCP_NODELAY`,
   `FIONBIO`), `connect` all, `select`, keep the first that becomes writable and close the rest. The winner is wrapped
   by `0x140dfaf70` in the same 0x2b8-byte stream class as the login socket.
3. Back in the tick: log "Connect time to instance server was N", store the stream at `+0xc288` and call
   `0x142148500`.

Client log lines from a good run:
```
Got Instance Details from login server
Connecting to instance server at 127.0.0.1:6112
Connect time to instance server was 0ms
```

## 3. The client's opening (plaintext, `0x142148500`)

`0x142148500` sets the state to 2 and sends two messages, then flushes:

| Id | Class | Fields | Source |
|---|---|---|---|
| 0x0F | `0x14316c420` (the login u32 class) | u32 ticket | 0x14 u32 at `+0x38` (info `+0x34`) |
| 0x0E | `0x1435bf9e8` | bytes[12] digest, u32 address, u32 0, u8 realm (0x40), u8 flag | address = 0x14's u32 at `+0x34`. Flag = global `0x14458e79f` |

The digest is computed with the hash family at `0x141fe1570` over the 8-byte constant at `0x142fbfbf8`, the 0x14
`Data8` (info `+0x78`) and the u32 `_time64`. The algorithm is not identified, and the server doesn't need to check it.

Real capture (first run, ticket still 0):
```
000F 00000000
000E 76240850BFE2B4956AD1D6B0 7F000001 00000000 40 01
```
30 bytes in total (`InstanceProtocol.OpeningLength`). `InstanceProtocol.TryReadOpening` parses it, and the Gateway logs the
ticket, digest, address, realm and flag. The ticket the client echoes is the one from 0x14 (verified with random tickets).

Sending in the client goes through `0x14216e940(this, &msg)`, which calls the message's vtable slot 1 on the stream
(under an SRW lock if `stream+0x242`). `0x140dfb8c0` flushes. The id is stored in the message object, so searching for
`htons(0x0f)` immediates finds nothing; follow the class vtables instead.

## 4. Switching to encryption: S→C 0x11

The server answers the opening with a bare **`00 11`** in plaintext. Dispatcher case 1 (id 0x11, class `0x14316c930`,
no fields):

- builds `0x142173610(key64, isClient=1)` → `0x140e03a30`, **the same Salsa20 pair as the login connection**, with km =
  the 64 key bytes from 0x14 (info `+0x38`), and installs it on the stream with `0x140dfbe30`. That function also
  decrypts any bytes already buffered after 0x11 in place.
- builds a second pair for a side channel at `stream+0xd0` with `0x14299dca0` → `0x14299db60`: the 64 key bytes are
  XORed with the 64 bytes at `0x142fe0da0`, bytes 0↔0x15, 4↔2, 0xd↔0x2a, 0x30↔1, 0x1d↔0x1e and 0x32↔0x13 are swapped,
  and `0x140e03a30(km', 1)` is called. This is **HNC**, a side channel hidden in carrier fields such as the 0x1c u32
  ([07](07-latency-visibility-movement.md) §2); the tick sends `0x14299dd80(…, 0xdd, …)` packets through it.
- clears the info pointer and records a timestamp.

From the byte after `00 11`, both directions are Salsa20: key `km[0:32]`, C→S IV `km[32:40]`, S→C IV `km[48:56]`.
Server side: `InstanceKeyring.Ciphers(ticket)` = `new ConnectionCiphers(HMAC-SHA512(secret, ticket BE), isClient: false)`.
The first decrypted client message (`00 7D …`) confirmed the keying.

With only 0x11 sent, the client sat silent for 45 s with the connection open. It waits for the server (0x1d, next doc).

## 5. The read loop (pump) and handler groups

`0x14299e440(pump, stream)`: read a u16 BE id (or a u8 if `pump[0]` is set), then offer it to each registered handler
group in `pump+0x18..+0x20` through the group's vtable slot 1. The first one that accepts wins. If none accepts:
"Unable to deserialise packet with pid …" (with a name if `pump+8` has a name table; this build has none). Groups are
registered with `0x14299e3b0(pump, group)` (which inserts at the front).

All four groups in the client share the generic handle slot `0x14299e300` and differ in their factory (slot 2).
`scripts/re/scanvt.py 0x14299e300` finds them:

| Group vtable | Factory (slot 2) | Connection |
|---|---|---|
| `0x1435d8b50` | `0x14219c350` | login |
| `0x1435d9d98` | `0x1421b5c40` | **instance** (about 300 ids) |
| `0x1435da300` | `0x1421c6150` | **world** (ids 0x51, 0x84, 0x161, 0x1a8–0x1d3, 0x1f0/1, 0x22f/230, 0x261/262, 0x26f–271, 0x285, 0x2b5–0x2c6) |
| `0x14363eb88` | `0x1428f74d0` | NetworkFile mode (`0x1428e9a60`) |

`0x14299e300(group, id, stream)`: id 0 → the group's owner slot 0 with no message. Otherwise factory(id) → message
object → its read slot 2 → owner `[1]` vtable slot 0 with the message. The instance pump at `this+0xc2b0` has the
instance group (`this+0xc2a0`, owner `this+8`) and the world group (`world+0x1c8`, registered by `0x142188cc0`).

## 6. The instance dispatcher

`0x14214a190(this+8, stream, msg)`: `switch(byte_table_0x142154e14[id − 0x0d])` for 0x0d ≤ id < 0x2fa. Ids that hit the
default case do nothing. `scripts/re/dispatch_ids.py` prints the mapping. The cases used so far:

| Id | Case | What it does | Doc |
|---|---|---|---|
| 0x0d | 0 | queue `+0xa1f0` | |
| **0x11** | 1 | start encryption | above |
| 0x17 | 2 | | |
| **0x1d** | 6 | area / instance init ("Client-Safe Instance ID", "Generating level …") | [03](03-area-and-world.md) |
| **0x21** | 10 (0xa) | enter area → `0x142156d80` → state 3 | [03](03-area-and-world.md) |
| **0x22** | 11 (0xb) | local player id → `world+0x980` (`0x142186fa0`) | [03](03-area-and-world.md) |
| 0x23 | 12 (0xc) | looks up an object type (no lasting effect seen) | |
| 0x33 | 15 (0xf) | inventory manager queries (types 0x17, 0x54) | |
| **0x3d** | 0x11 | passive slots | [05](05-inventories-and-ui.md) |
| **0xc0** | 0x35 | add inventory → `0x141d625f0` | [05](05-inventories-and-ui.md) |
| 0xc1 | 0x36 | inventory → `0x141d62700` | |
| 0xc2 | 0x37 | inventory → `0x141d62710` | |
| 0xc3 | 0x38 | gems/sockets of existing inventories (`0x142173fc0`) | |
| 0xc4 | 0x39 | inventory list op (`0x142174500`) | |
| 0xc6 | 0x3a | inventory type 0xb lookup | |

Before the switch the dispatcher returns early when the message is null (unless the state is already 1 it
disconnects with code 0xf through `0x142149ca0`, which sets state 1) and when the owner's vtable `+0x70` filter consumes it.

## 7. The tick (`0x142148880`) once connected

- When `+0xe1f9` is set (the area finished generating) it sends **C→S 0x7d** (class `0x1435bf8a8`, two u32 = tile hash
  and doodad hash, from world `+0x114`) and flushes. See [03](03-area-and-world.md#2-c→s-0x7d-area-generated).
- It pumps messages while the pump accepts them (`0x14299e440` in a loop), unless the stream is closing (`+0x251`).
- In state 3 it sends **C→S 0x1b** (class `0x1435c0358`: u8 (a counter from world `+0xb98`, reset after sending, capped at 255), then
  4 more bytes) every 100–500 ms, then also 0x31 (skill/inventory actions) and 0xc7 (queued u16 + u8) when it has
  them. Seen live: `001B 00 702FB779`, `001B 00 0324B665`, ….

C→S messages seen so far: 0x0F, 0x0E (plaintext), 0x7d, 0x1b, 0x25, 0x015a, 0x01ef, 0x0099, 0x004e, 0x0134
(chat channel join, [10](10-chat-and-commands.md) §2), 0x0129. The Gateway answers
every 0x1b with 0x1c, whose u32 must carry idle HNC bytes: see [07](07-latency-visibility-movement.md).

## 8. Our Gateway

`src/GameServer.Gateway/Connection/Program.Connection.cs` (`HandleAsync`), per connection. `Program` is one partial class split by feature: `Program.cs` holds the startup and the shared constants and catalogs, `Program.<Feature>.cs` the handlers (`Messages` the C→S dispatch, `Combat`, `Monsters`, `Items`, `Stash`, `Npcs`, `Travel`, `Portals`, `Commands` …), `ClientState.cs` the per-connection state:

1. read until `TryReadOpening` succeeds (30 bytes) → `keyring.Ciphers(opening.Ticket)`
2. send `00 11` plaintext
3. send `AreaInstance(1, 0x235c, seed 1, level 0)` encrypted (0x1d)
4. decrypt and hex-log client bytes. On the first chunk starting `00 7D`: send 0x22, 0x2b9, 117 × 0xc0, 0x3d and,
   if `Game_GATEWAY_ENTER=1`, 0x21 (see [03](03-area-and-world.md), [04](04-player-entity.md), [05](05-inventories-and-ui.md))

### Fixed-message boundaries across TCP reads

The instance protocol has no message-length prefix; one TCP read can split a message at any byte.
The primary 3.29.3b writers establish two additional complete client-message lengths:

| Message | Bytes including opcode | Primary writer | Fields after the u16 opcode |
|---|---|---|---|
| C?S 0x020a | 3 | `0x140e29080` | one byte |
| C?S 0x0059 | 16 | `0x141f935a0` | 4, 2, 2, 4, 1 and 1 bytes, unconditionally |

The 0x020a send site is `0x140d3c409..0x140d3c43b`, with vtable `0x14345ef10`.
The 0x0059 sender is `0x142160020`, with vtable `0x1435bfe78`.
A five-byte observation of 0x0059 is a partial read, not its complete length.
`KnownClientLength` now retains each incomplete prefix and consumes exactly one complete message.
It leaves the existing four-byte 0x0099 and chat-channel handling intact.

Tests split these messages at every byte, coalesce them with ticks and wide-string chat, and feed one byte per read.
They also preserve the captured 0x0099 + two ticks + 0x01ef sequence as four separate messages.
The same fixed-length implementation passed ten integrated native area transfers with subsequent chat intact.
That evidence belongs to the larger Phrecia integration; this extracted branch is independently covered by tests.
It does not prove which message caused the earlier intermittent wide-string error.
Unknown message layouts still require primary evidence; their lengths are not guessed.

Env: `Game_GATEWAY_PORT` (6112), `Game_INSTANCE_SECRET` (data/instance-secret.bin), `Game_GATEWAY_ENTER`.
