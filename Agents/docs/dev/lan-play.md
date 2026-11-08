# Playing with other machines: LAN and tunnels

Two or more PCs playing together: one runs the servers (the host), the others run the patched client and connect to
it. This works because the client learns every address at run time — the gateway list names the login server, the
login 0x14 names the instance server ([010](../re/010-live-login-to-instance.md)), and the patching server's welcome
names the web root. Nothing in the protocol pins the game to 127.0.0.1; the shared-instance world, parties and player
trade ([28](../protocol/28-player-trade.md)) were verified with two clients.

The same setup covers friends outside the local network, through a tunnel. Only the address the clients are told
changes.

## The one command (host)

```powershell
.\scripts\dev\live.ps1 -Lan                                # same LAN: uses this PC's LAN address
.\scripts\dev\live.ps1 -Lan -PublicAddress 100.64.1.23      # Tailscale/ZeroTier (virtual LAN)
.\scripts\dev\live.ps1 -Lan -PublicAddress me.frp.example.com -PublicPort 26000   # port-mapped tunnel
```

`scripts/dev/build-host-tool.ps1` publishes the same thing as a window (`dist\host\LocalExileHost.exe`): the address,
the public ports and one 开服 button, with the script's output streaming into the window.

- The servers bind `0.0.0.0` (`Game_BIND`), the patching protocol included (`Game_PATCH_BIND`).
- Remote clients are told `-PublicAddress` (`Game_GATEWAY_HOST`). Without it: the IPv4 address of the interface that
  holds the default route — the network other machines can reach, not a virtual adapter that happens to come first.
- `-PublicPort` is only for a tunnel that maps a **different** public port onto the Gateway's: clients are told the
  public one (`Game_GATEWAY_PUBLIC_PORT`), the Gateway keeps listening on its own. `-PublicLoginPort` is the same for
  the login port.
- Only **two** tunnels are needed: the login port and the instance port. A remote client never contacts the host's
  patching server — the player's tool answers that handshake itself with its own GGPK's hash — so 12995 stays local.
- The host's own client is unchanged: the GGPK's gateway list stays on 127.0.0.1 whatever the tunnel says, and a
  connection from this PC is handed the loopback instance address. The addresses are decided per connection
  (`InstanceTransfer.ConnectInstance`, `Modules/Core/Game/World/Travel.cs`): a peer on `-LocalPeers` (the env var
  `Game_LOCAL_PEERS`, loopback by default) gets `127.0.0.1:<local port>`, everyone else `-PublicAddress`/`-PublicPort`.
- A tunnel client that connects from another machine can be told apart by its address; one whose `frpc` runs on the host
  PC cannot be, because a remote player then arrives from loopback too. That topology starts with `-LocalPeers none`,
  which counts no peer as local and sends every client to the tunnel's address. The host's own client then dials the
  tunnel as well, which costs one hop through the node but keeps the host and its players on one endpoint.
  `logs/live-auth.log` prints the endpoint each login connection was handed, and whether the peer counted local.

Allow the ports through Windows Firewall once (plain local play never needed it: the servers bound loopback):

```powershell
netsh advfirewall firewall add rule name="LocalExile" dir=in action=allow protocol=TCP localport=20493,6112,12995,8080
```

## On a remote player's PC (once)

They need the same 3.29.3c client and the host's patched executable: `GameServer.Auth patch` replaces the key blobs
embedded in the client (doc 008), so the simplest path is that the host copies their `Client/Game.patched.exe`, and
the remote player installs the client data as in [GettingStarted](../../GettingStarted.md) steps 3–5 (keygen and patch
skipped — the executable is already patched).

Then the host builds the tool and hands the **whole folder** over (`scripts/dev/build-connect-tool.ps1`, about 110 MB,
no .NET needed on the player's PC). Publish it as a folder, not a self-extracting single file: that shape is what
antivirus software quarantines.

```powershell
.\scripts\dev\build-connect-tool.ps1          # writes dist\connect\LocalExileConnect.exe beside its DLLs
```

The player puts the folder beside their `Content.ggpk` and the game's `Game.patched.exe`, then:

1. types the host's address (what `live.ps1 -Lan` printed) and clicks **测试连接** — the login and instance ports are
   dialled, so a green answer means the route works;
2. clicks **配置客户端** — every gateway in their GGPK points at the host, the original is saved as
   `gateway_list.original.txt`, and the game's GGPK root hash is written beside it;
3. clicks **启动游戏** — the tool answers the patching hello itself with that hash (`No patching required`) and starts the
   client against `127.0.0.1`, so **the patching port needs no tunnel at all** and the client never contacts the
   official server; **还原原版** puts the original list back.

The same three actions from a command line, for a shortcut or a script:

```powershell
LocalExileConnect.exe --check 192.168.1.10            # [loginPort] [patchPort] optional
LocalExileConnect.exe --set 192.168.1.10:20493
LocalExileConnect.exe --restore
```

Success: the client reaches the login screen, an account created on the host
([GettingStarted](../../GettingStarted.md) step 8) logs in, and both characters play together.

An account and a character are needed per player: `GameServer.Host` + `GameServer.DevClient`
([dev server](dev-server.md), GettingStarted step 8) creates them, and the client's Email field takes the account name.

## Why the host's own GGPK is written too

The patch welcome carries the host's GGPK root hash and the client patches when its own differs. Every player's
`gateway_list.txt` therefore has to be **byte-identical**, not merely reachable: `-Lan` writes the same
`<public address>:<login port>` into the host's GGPK (and re-saves the hash) that the tool writes into each player's, so
every copy hashes the same and nobody is sent to patch (whose web root answers 404 on purpose). Without `-Lan` the
list stays on 127.0.0.1 as before.

## Tunnels, in order of how little work they are

| Kind | What to forward | Notes |
|---|---|---|
| Virtual LAN (Tailscale, ZeroTier, WireGuard) | nothing — the tunnel is the network | `-PublicAddress` is the host's tunnel address; clients reach it directly. No port mapping, no firewall rule. |
| frp / nps / rathole and similar | TCP 20493 (login) and 6112 (instance) | The instance port is dialled by the client itself, so it needs its own mapping: 0x14 hands out address **and port**. If the public port differs, add `-PublicPort`. The patching port is not needed. |
| Hosted Chinese TCP tunnels (SakuraFrp / natfrp, 花生壳, natapp) | the same two ports, one tunnel each | They assign a random public port per tunnel, on a shared node domain. Give the node domain to `-PublicAddress`, the public login port to `-PublicLoginPort`, the public instance port to `-PublicPort`. Two tunnels are enough on a free plan that allows no more. |
| Cloudflare Tunnel / ngrok-style HTTPS only | not enough | The game speaks raw TCP on all three ports and the login is not HTTPS ([007](../re/007-login-is-custom-tcp.md)); a TCP tunnel is required. |

A hosted tunnel with random ports, host side (SakuraFrp's panel shows two public `node:port` addresses):

```powershell
.\scripts\dev\live.ps1 -NoExtract -Lan -PublicAddress your-node.example.com `
    -PublicLoginPort 24567 -PublicPort 24568
```

The player's tool then takes the node domain and the public login port; the instance port it dials comes from the login
answer, so its own field is only used by the connection test.

Port 8080 (the patch web root) only matters if a client decides to patch: it serves `gateway_list.txt` and answers
404 to everything else on purpose, and the welcome's root hash matches an untouched client, so in practice it is
never fetched. Through a tunnel, add a mapping for it too if a client ever does patch.

## Rules and limits

- **League**: trade and shared loot need the full league outside SSF. SSF instances belong to one stable character ID
  and refuse everyone else ([league modes](../game/league-modes.md)), so players share Standard (or Hardcore).
- **Network mode**: leave the client on Predictive (Options → Game → Network Mode). Lockstep is off by default, and a
  tunnel connection is never "confirmed local" (`LockstepLink.Confirmed` checks loopback, and the client was told
  another address). A lockstep and a predictive player can still share a town
  ([07](../protocol/07-latency-visibility-movement.md) §6).
- **Latency**: this server corrects a predictive client at each action start, so a tunnel adds its round trip to every
  correction. Under about 100 ms is comfortable; much more shows as rubber-banding between actions.
- **Control port**: Auth ↔ Gateway control stays loopback (`Game_AUTH_CONTROL_PORT`); only the client-facing ports are
  opened.
- **Secrets**: the patched executable carries the host's login keys; share that file, never `data/login-keys.json`,
  `data/instance-secret.bin` or the saves database. Anyone with the executable can log in to the host's server.
- **`-PortBase` stacks**: every port shifts; the remote player's two commands must name the shifted login and patch
  ports (`data/stack.json` lists them).

## What players can do together

| In game | How |
|---|---|
| see each other | both in the same instance: a town is shared (every town is one instance), a private area is one copy per player unless they join the party's |
| talk locally | type without a prefix: everyone in your own instance sees `Name: text` |
| global / trade | `#text` and `$text`: everyone on the same channel number (both sides start on channel 1) |
| party chat | `%text`: your party |
| whisper | `@name text` |
| party | `/invite <name>` (or right-click a player) → they accept: the party list (0xa9) and the party bonus (0x231) follow; a third invite joins the same party |
| leave / kick / promote | `/leave`, `/kick <name>` and `/promote <name>` — the last two for the leader; kick sends the removed member the same 0xaa a leave does, promote reorders the party list so that member becomes the leader |
| join a party member's instance | `/travel <character name>` — the same area *and* instance they are in |
| decline an invitation | `/decline` — the invitations waiting for you are dropped; the inviter is told with 0xaa only when that party id is the invitation's alone (an invite from someone already in a party reuses their id, and 0xaa on it would remove *them*) |

Known gaps, from the client side: the party panel's area column shows "Travelling..." instead of the member's area,
and its travel button stays disabled. The panel has no source for a member's area at all — measured 2026-11-08 with a
real second client: same area shows nothing, another area shows "Travelling..." for both a player and a bot.
[`schema.md`](../protocol/schema.md) (the client's own S→C read table) settles what is not the source: 0xa9 carries no
area field, the wstr at 0xa7 `+0x90` is ignored by the panel whether filled with the area name or its id, and there is
no S→C 0xac in that table at all (an earlier "dispatcher case 0x10" reading came from the 3.29.3b case table and does
not apply here). Setting 0xa9's `+0x6f` to 1 made the client drop its instance connection, so that byte is not it
either. **0xa4** (online known player) was then tried in two shapes and the client answered "Protocol violation reading
wstring from the network" both times, so it is not it either; that message is no longer sent. The party panel's area
column and its travel button therefore stay unimplemented — use `/travel <name>`
([28](../protocol/28-player-trade.md) "Open", [roadmap](../../roadmap/world.md)).

**Capturing the missing packets takes one click each.** The Gateway logs every client message it does not handle as
`<= unframed 0x....` with its bytes in `logs/live-gateway.log`. That is how the party panel's Kick became C→S **0x94**
(a compact wstr member name, implemented) and how a friend invite showed up as **0x179** (still unhandled). The panel's
Decline and Promote have not been seen yet; one click each is enough to implement them.