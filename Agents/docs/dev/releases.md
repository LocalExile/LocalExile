# Releases

A release is a zip for players: unzip, run `LocalExile.exe`, and with their own 3.29.3b client they need nothing else.
Contributors keep working from a checkout with `live.ps1` ([GettingStarted](../../GettingStarted.md)).

## Making one

1. Actions > **Release** > Run workflow, on `main`, with the version (`0.2.0`, or `0.2.0-beta.1` for a pre-release).
2. The workflow (`.github/workflows/release.yml`, a hosted Windows runner) builds the package, runs no tests,
   attests the zip and creates a **draft** release with the notes, the zip and its `.sha256`.
   It cannot check the real client: the runner has neither the client nor `data/`.
3. Download the draft's zip, extract it into a new folder and play through it with the real client.
   Use a separate folder from any checkout, and close every other client first.
4. Write the Highlights in the notes, then publish. Publishing creates the tag `v<version>` on the built commit.

To build it again, delete the draft and start the workflow again.
The same package builds by hand in a clean checkout: `.\scripts\release\build-release.ps1 -Version 0.2.0`
(in `artifacts\release\`; it needs the .NET SDK and npm).

## Versions

SemVer, `0.x` for now: the minor for a release with features or anything that breaks, the patch for fixes.
The workflow prints the bump the commits suggest; the version used is always the one typed.
The client it supports (3.29.3b) is in the notes and `release.json`.
Dev builds keep the default version and no commit, so a commit does not restart a watched Gateway.

## Commits and notes

Commit subjects on `main` follow [Conventional Commits](https://www.conventionalcommits.org): `feat:`, `fix:`, `perf:`,
`docs:`, `refactor:` become the notes' sections; `chore:`, `ci:`, `build:`, `test:`, `style:` are left out; `!` or a
`BREAKING CHANGE:` line marks a breaking one.
`scripts/release/release-notes.ps1` reads the non-merge commits since the last tag, and other subjects go under Other,
so an unconventional one breaks nothing and nothing is checked before a merge.
Contributors are named by GitHub login, or by commit name when GitHub has no account for it; never by email.
Merge commits stay; no history is rewritten.

## What is in the package

| Path | What |
|---|---|
| `LocalExile.exe` | the launcher (`src/LocalExile`), one self-contained file |
| `app/` | self-contained win-x64: the Auth, Patch and Gateway servers and the extraction tools, sharing one runtime |
| `Modules/`, `Mods/Binaries/`, `Leagues/` | built modules and mods; leagues as source, which the Gateway compiles when it starts |
| `Tools/python/`, `Tools/node/`, `Tools/ooz/` | the embeddable Python and Node (pinned by hash in `build-release.ps1`), ooz-wasm |
| `Tools/extract-manifest.json` | `extract-data.ps1`'s manifest, with each entry's program |
| `scripts/re/*.py`, `scripts/re/levelgen/gate-baseline.json`, `Agents/docs/re/components.json`, `data/schema/` | what the generators and servers read at those paths |
| `licenses/`, `README.txt`, `release.json` | licenses and the commit it was built from, the player's guide, the version |

It keeps a checkout's relative paths, so the servers and tools run unchanged, with the install as working directory.
The zip's one folder is always `LocalExile/`, so an update extracts the new zip over the old; `data/` (saves, keys, extracted data) and `Client/` are not in it, so they stay.
`build-release.ps1` refuses a package with game files, keys or a save database, or whose client command differs from below.

A new file that a server or generator reads outside `data/` must be added to `build-release.ps1`, or a release lacks it.
A new `dotnet` tool in the extraction manifest must be published there too; the build fails if it is not.

## The launcher

`LocalExile.exe` always works from its own folder. In order: it checks the client (`Client\`, or asks for a folder, then
to copy it or link to it), runs the extraction with the same manifest and stamps as `extract-data.ps1`, makes the keys and
`Game.patched.exe`, creates an account if there is none, points the client's login list at the local server, starts the
servers, and starts the client. It stays running until the client closes, and the servers end with it (a job object:
also on a crash or a closed window).

**The client starts only one way:** `Client\Game.patched.exe --patching-server 127.0.0.1:<patch port>`, in `ClientLaunch.cs`.
Any other start can update it. The version check needs both the login key blobs (`ClientImage`) and the HNC mask's hash.

The login list is the one client archive write, the same as `live.ps1`'s: only when it does not already name the local
server, with the original kept in `data/gateway_list.original.txt`. That is why copying is the default: with
`--client-mode link` it writes to the player's only copy of 3.29.3b.

`LocalExile.exe --help` lists the commands. `setup` does everything before the servers, `stop` ends this install's
processes, `--no-client --port-base <n>` runs just the servers on other ports. It asks GitHub once per run whether a newer
release exists and only says so (`--no-update-check` turns it off); it never downloads anything.

### For a GUI: `--json`

With `--json`, standard output is one JSON object per line and nothing asks a question.
Every event has `schema` (1), `event` and `time`; a change that breaks a reader raises `schema`.

| `event` | Other fields |
|---|---|
| `started` | `version` |
| `step` | `id`, `message`: a new part of the work |
| `progress` | `id`, `message`, optional `current`, `total`: at least every 10 s during long work |
| `info`, `warning`, `done` | `message` |
| `value` | `name`, `value`: the answer of `version`, `client-command`, `check` |
| `needs-input` | `option`, `message`, optional `suggestion`: run again with that option |
| `ready` | `patch`, `login`, `gateway`, optional `clientPid` (none with `--no-client`) |
| `error` | `code`, `kind`, `message` |

Exit codes: 0 done, 1 unexpected, 2 usage, 3 client not usable, 4 extraction failed, 5 a server failed, 6 a port in use or
already running, 7 input needed, 8 the client failed to start.
A GUI keeps the process running while the game runs: ending it ends the servers and the client.
`--password-stdin` takes the new account's password as the first line of standard input.

## Limits

- Not code-signed: SmartScreen and antivirus may warn, and `Game.patched.exe` is a modified executable.
- Walk-map capture and hot reload are for checkouts: a release has neither Frida nor the SDK.
  Modules cannot be rebuilt in a release, and mods from elsewhere go in `Mods/Binaries/` built.
- The extraction keeps its file cache in `Tools\re\out\ot`, so it needs space beside the install.
