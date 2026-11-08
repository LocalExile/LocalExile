# 007 — Login is NOT HTTPS: it is a custom TCP protocol (verified 2026-10-15)

Re-derived independently; supersedes the H2.1 "TLS/HTTP2 login" hypothesis in 002.

## Evidence
- `Client/logs/Client.txt`: `Async connecting to par.login.Game.com:20493` →
  `Connected ... in 110ms` (plain async TCP connect log line, no HTTP/TLS lines).
  The same login connection later delivers `Got Instance Details from login server`
  → `Connecting to instance server at <ip>:6112`. Every zone change repeats that
  (login connection hands out the instance address), so area transitions, character
  list, etc. all ride this one connection.
- `Game.exe` (3.29.3b, sha256 78a0e227...48d5) strings: libcurl (HTTP/S, used by
  the *patcher*), Schannel, Crypto++ (RTTI `CryptoPP::Integer` present). No login URLs.
- Code anchors (image base 0x140000000), found with `scripts/re/xr.py`:
  - `Async connecting to` xref `0x1421447a0`
  - `Got Instance Details from login server` xref `0x140d57134`
  - `Connecting to instance server` xref `0x14214ea9e`
  - env/config key registration table around `0x140e2f05b` (`GGG_LOGIN_SERVER_PORT`...)

## Consequences
- `GameServer.Auth` (HTTP/Kestrel) cannot serve the real client's login. It must become a
  raw TCP server speaking the client's binary protocol; the MITM proxy (HTTP CONNECT) does
  not apply to it. Redirect of login = `gateway_list.txt` (host:port) only.
- Login handshake and crypto: solved in `008-login-handshake.md`; `GameServer.Auth` is now that raw TCP server.
  `:6112` is still unknown (handler at `0x140d57090` parses Instance Details).

## Tooling status
- REA's Ghidra auto-analysis of the 79 MB exe exceeds 1 h (provider_timeout). Use the
  persisted headless project: `Tools/ghidra-projects/run.ps1` (project `Game3293`).
- Fast static helpers: `scripts/re/xr.py <string>` (xrefs), `scripts/re/dis.py <va> [before] [len]`.
