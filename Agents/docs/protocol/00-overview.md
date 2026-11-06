# 00 — End-to-end overview: from launch to the in-game HUD

Client: Path of Exile **3.29.3c**, `Client/Game.exe` (image base `0x140000000`), run as the patched copy
`Client/Game.patched.exe`. Every address in these documents is a Ghidra VA in that image. State of 2026-10-16: the
patched client logs in to our servers, lists characters, connects to our instance server, generates Lioneye's Watch,
creates the local player and shows the in-game HUD.

> **3.29.3c id shift.** Most protocol documents use 3.29.3b message ids as their reference; pages updated for
> 3.29.3c say so. 3.29.3c inserted two world messages before the old frame pair, so every id from `0x2b5` is +2:
> frame end/length `0x2b7`/`0x2b8`, create object `0x2bb`, component update `0x2bc`, remove object `0x2bd`,
> leave-area ack `0x2cd`. The [3.29.3c diff](../re/3.29.3c-client-diff.md) has the full table.

![first in-game frame](../re/evidence/2026-10-16-first-in-game.jpg)

## Processes and ports

| Our process | Port | Role | Client side |
|---|---|---|---|
| `GameServer.Patch` | tcp/12995 + http/8080 | patching protocol (version 7) and a web root that serves only `gateway_list.txt` | started with `--patching-server 127.0.0.1:12995` |
| `GameServer.Auth serve` | tcp/20493 | login server: key exchange, account login, character list, instance assignment | host:port from `gateway_list.txt` |
| `GameServer.Gateway` | tcp/6112 | instance (game) server: area, player, inventories, enter area | host:port from login message 0x14 |
| `GameServer.Host` | tcp/7000 | separate JSON-lines dev protocol (not the official client) | `GameServer.DevClient` |

Auth and Gateway share `data/instance-secret.bin`. The instance key for a session is
`HMAC-SHA512(secret, ticket)`, and the ticket travels in login 0x14 (see [01](01-login.md) and [02](02-instance-connection.md)).

## The whole conversation

```mermaid
sequenceDiagram
    participant C as Client (Game.patched.exe)
    participant P as Patch :12995
    participant A as Auth :20493
    participant G as Gateway :6112
    C->>P: hello v7
    P->>C: welcome (root hash of local GGPK, web roots = http://127.0.0.1:8080/)
    Note over C: "No patching required" → login screen
    C->>A: 0x0002 ephemeral DH key (plaintext)
    A->>C: 0x0002 server key + DSA signature (plaintext)
    Note over C,A: Salsa20 from here (key = SHA-512(DH2 secret))
    C->>A: 0x03 account login (SHA-256 of UTF-16LE password)
    A->>C: 0x04 result 0, flags 0x20 (verified)
    A->>C: 0x1b character list push (empty)
    C->>A: 0x18 (character screen opened)
    A->>C: 0x15 character entries
    C->>A: 0x0e (Play)
    A->>C: 0x14 instance: host, port, address, area hash, ticket, 64-byte key
    C->>G: 0x0F ticket, 0x0E hello (plaintext)
    G->>C: 0x11 start encryption (plaintext)
    Note over C,G: Salsa20 from here (key = the 64 bytes from 0x14)
    G->>C: 0x1d area (instance id, area hash, seed, level)
    Note over C: generates 1_1_town
    C->>G: 0x7d tile hash, doodad hash
    G->>C: 0x22 local player id
    G->>C: 0x2b9 create object (the Witch, component blob)
    G->>C: 117 × 0xc0 add inventory (one per Inventories.dat row)
    G->>C: 0x3d Atlas tree presets, 0x33 Passive Skill Tree
    G->>C: 0x21 enter area
    G->>C: 0x1a9 player enters wake zone (drawn, updates)
    Note over C: world activation, player bound, state 3, HUD
    loop every 100–500 ms
        C->>G: 0x1b tick
        G->>C: 0x1c ack (u32 = idle HNC bytes)
    end
    C->>G: 0x25 use skill at location (Move, on click)
    G->>C: 0x1ab action started (echo; client walks)
```

## Documents

| Doc | Content |
|---|---|
| [01-login.md](01-login.md) | login connection: handshake, framing, message set, our login server, live flow |
| [02-instance-connection.md](02-instance-connection.md) | instance connect path, opening, encryption switch, read loop, handler groups, dispatcher, state machine |
| [03-area-and-world.md](03-area-and-world.md) | 0x1d area, 0x7d, world message queueing, 0x22, 0x21, enter-area handler, spatial index |
| [04-player-entity.md](04-player-entity.md) | object type register, MurmurHash2 key, 0x2b9, component blob, each component's serializer |
| [05-inventories-and-ui.md](05-inventories-and-ui.md) | inventory manager, 0xc0, Inventories.dat, 0x3d Atlas tree presets, UI prerequisites |
| [07-latency-visibility-movement.md](07-latency-visibility-movement.md) | tick/ack 0x1b/0x1c, the HNC side channel, wake zone 0x1a9 (visibility), movement 0x25/0x1ab, world object messages |
| [08-area-transitions.md](08-area-transitions.md) | exit objects (AreaTransition) placed in an area |
| [09-area-change.md](09-area-change.md) | changing area: 0x24 → 0x2b2 → 0x2cb → 0x27, instance chooser 0xae/0xaf, instance registry |
| [10-chat-and-commands.md](10-chat-and-commands.md) | chat input 0x15, server text (0x0d message box, 0x12d notices), stats update 0x1b3, dev slash commands |
| [06-debugging-log.md](06-debugging-log.md) | chronological log of every live run, crash and fix |
| [address-index.md](address-index.md) | every client address used, by area |
| [../re/tooling.md](../re/tooling.md) | Ghidra headless scripts, Python helpers, Frida via REA, GGPK dump tool |
| [../dev/live-testing.md](../dev/live-testing.md) | running a live test safely, env vars, what to expect, troubleshooting |

Older notes: `Agents/docs/re/003`–`004` predate the login work and contain errors (for example "login is HTTPS"). `Agents/docs/re/007`–`010`
are the working notes these documents were consolidated from. When they disagree, these documents are newer.

## Conventions

- **Message framing** (login and instance): `u16 id` big-endian, then fields, no length prefix and no terminator. A
  reader has to know every layout. Field encodings are in [01-login.md](01-login.md#field-encodings).
- **Message fields are big-endian. The 0x2b9 component blob is little-endian** (it is read with memcpy).
- `+0x…` means a field offset inside the client object named in context (message object, component, instance client).
  Message objects store `u16 id` at `+8` and fields from `+0x10`.
- "Case N" means a case of the instance dispatcher's switch, which is not the message id. `scripts/re/dispatch_ids.py`
  maps ids to cases.
- Code: `src/GameServer.Core/Protocol/Login/*`, `src/GameServer.Core/Protocol/Instance/*`,
  `src/GameServer.Core/Protocol/Patch/*`, servers in `src/GameServer.{Auth,Gateway,Patch}`. Tests in
  `tests/GameServer.Core.Tests`.
