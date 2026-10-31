# Dev game server (own protocol)

A playable server core that does **not** speak the official client's protocol. It exposes accounts,
characters, a world and instances over one-JSON-object-per-line TCP, for building and testing game logic.

```powershell
dotnet run --project src/GameServer.Host            # 127.0.0.1:7000, accounts in data/saves.db
dotnet run --project src/GameServer.DevClient       # interactive client
```

Env: `Game_DEV_PORT`, `Game_DEV_BIND`, `Game_SAVES_DB` (the database), `Game_DEV_DB` (an older accounts.json, imported once).

## Ops (request `{"op":...}` → reply `{"ok":true,...}` or `{"ok":false,"error":"..."}`)

| op | fields | notes |
| --- | --- | --- |
| `register` | name, password | name 3–32 `[A-Za-z0-9_-]`, password ≥ 4 |
| `login` / `resume` | name+password / token | sets the connection's account; returns token |
| `characters` | | list of the account's characters |
| `accounts` | | every account (name, created) with its characters (class, level, league, last town, last played, Hardcore death); no login needed, no secrets: for local tools such as the desktop launcher |
| `create_character` | name, class | class: scion marauder ranger witch duelist templar shadow |
| `delete_character` | name | |
| `enter_world` | name | joins the character's last area; returns instance `{id, area, seed, monster_level, players}` |
| `enter_area` | area, [monster_level] | zone change; towns (`*_town`) are shared, other areas get a fresh private instance |
| `who` | | current character + instance |

Example session in the client:

```
register name=alice password=pw1234
login name=alice password=pw1234
create_character name=Exile class=witch
enter_world name=Exile
enter_area area=1_1_2
```

## Layout

- `GameServer.Core` — `AccountService`, `InstanceManager`, `GameSession` (protocol logic, no sockets)
- `GameServer.Storage` — `JsonAccountStore` (atomic JSON file)
- `GameServer.Host` — TCP host; `GameServer.DevClient` — console client
- Tests: `tests/GameServer.Core.Tests` (service, store, instances, and full TCP flows)

Passwords are PBKDF2-SHA256 (50k iterations, per-account salt); sessions are random 24-byte tokens, 12 h expiry.

## Official-client login server (`GameServer.Auth`)

Separate from the dev protocol: this is the start of serving the real client. It completes the login connection's
key exchange (Agents/docs/re/008), then decodes the client's messages with `LoginCodec` (Agents/docs/re/009) and logs one line per
message. After an id it doesn't know, it can't find the next message boundary (there's no length prefix), so it
hex-dumps the rest of that connection. It answers account login (0x03 → 0x04) against the same account store as
`GameServer.Host` (`data/saves.db`, [saves](saves.md)), so register accounts with the dev client first. Each account also stores a verifier
for the client's SHA-256 password digest, and accounts made before that get it on their next dev-protocol login. Don't
change accounts in Host and Auth at the same time: each keeps its own in-memory copy of the file.

```powershell
dotnet run --project src/GameServer.Auth -- keygen Client/Game.exe                  # writes data/login-keys.json (private keys; untracked)
dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe
dotnet run --project src/GameServer.Auth                                            # serve on tcp/20493 (Game_LOGIN_PORT)
dotnet run --project src/GameServer.Auth -- passwd devtester " "                    # set a dev account's password (any length)
```

`passwd` changes both stored hashes (dev login and the client's digest) with a new salt; restart Auth afterwards
(`live.ps1 -Only Auth`). While Auth runs, its build folder is locked: run a copy built elsewhere (`dotnet build
src/GameServer.Auth -o <dir>`, then `dotnet <dir>/GameServer.Auth.dll passwd …` from the repository root).

`patch` never overwrites its input. It replaces only the client's DSA verify key `y` and the server static DH value,
keeping their sizes. To point the client at this server, use the `gateway_list.txt` host:port override (Agents/docs/re/004, 007).

## Patching server and web root (`GameServer.Patch`)

`GameServer.Patch` runs the client's patching protocol on tcp/12995 (`Game_PATCH_TCP_PORT`) and an HTTP web root on
`http://0.0.0.0:8080` (`Game_PATCH_URL`). `scripts/dev/live.ps1` starts the client with
`--patching-server 127.0.0.1:12995`; the welcome carries the GGPK's root hash, so the client logs "No patching
required" and goes to the login screen. **Client files must never be updated** — an update would replace the patched
client.

Defaults (no opt-in):

- The welcome advertises our own web root for both the primary and backup roots: `http://127.0.0.1:8080/` (the listen
  URL with `0.0.0.0`/`+`/`*` as `127.0.0.1`; override with `Game_PATCH_WEB_ROOT`). It never names `patch.poecdn.com`.
- The web root serves only `gateway_list.txt` (the UTF-16 file `Game_GATEWAY_LIST` names, else the two local
  gateways in the code) and answers **404** for everything else. Nothing is proxied or redirected to the real CDN, so even a client
  that decides to patch cannot download real files. Requested paths are logged (`[patch] GET …`).

Opt-in CDN passthrough — only to repair stale local data on purpose, with a backup of `Client/`:

```powershell
$env:Game_CDN_PASSTHROUGH = '1'      # or: dotnet run --project src/GameServer.Patch -- --cdn-passthrough
```

Then requests other than `gateway_list.txt` are passed to `Game_CDN_MIRROR` (default `https://patch.poecdn.com`) with
the client's Basic auth, and the server logs `CDN PASSTHROUGH ENABLED`. The welcome still points at our web root.
