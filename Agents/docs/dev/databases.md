# Databases

How the servers store data, and how a change to it is made: SQLite files split by owner, built from SQL kept in git
(the TrinityCore/AzerothCore/cMaNGOS model, [#734](https://github.com/LocalExile/LocalExile/issues/734)).

## The files

Every database is a SQLite file under `data/` (git-ignored), created and brought up to date by the server that opens it.

| File | Holds | Opened by | Override |
|---|---|---|---|
| `auth.db` | accounts | Auth, Host, Gateway (attached to `characters.db`) | `Game_AUTH_DB` |
| `characters.db` | character saves, stashes, the login character list, friends, deleted characters | Gateway, Auth, Host | `Game_CHARACTERS_DB` |
| `client.db` | the client's 941 tables (`Mods`, `Stats`, `WorldAreas`, ...), untouched rows plus our corrections | Gateway, Auth | `Game_CLIENT_DB` |
| `world.db` | the game content: settings, unique weights, catalogues, drop tables, roll range and limit overrides | Gateway | `Game_WORLD_DB` |

A connection to the player databases opens `characters.db` and attaches `auth.db` as `auth`, so a query names a table of
either without its database. Rows that must be written together live in one file: a transaction across both files is
atomic in each file only. A characters database somewhere else (`Game_CHARACTERS_DB`, a test's scratch file) keeps its
accounts beside it as `<name>-auth.db`, unless `Game_AUTH_DB` says otherwise.

## SQL in git

```
sql/
  base/<db>/<table>.sql                     schema only: CREATE TABLE (and its indexes); never rows
  updates/<db>/YYYY_MM_DD_HHMMSS_<name>.sql  merged changes, data and schema, applied in name order
  updates/pending_<db>/<any name>.sql        a pull request's changes, before the merge renames them
```

A change to a database, rows or schema, is a new file in `sql/updates/pending_<db>/`; pending files apply in name
order, so a leading number (`1_x.sql`, `2_y.sql`) orders several. After the merge the `SQL pending updates` workflow
renames each to `YYYY_MM_DD_HHMMSS_<name>.sql` (the number dropped), after every merged file, and moves it to
`sql/updates/<db>/`; it pushes that commit to `main` with a write deploy key (the `SQL_PENDING_DEPLOY_KEY` secret), which the main ruleset
lets bypass it: GitHub Actions' own token cannot be a ruleset bypass actor. The client
tables are the exception: `client-sql.ps1` writes them dated, straight to `sql/updates/client/`. A merged file never changes and nothing is ever squashed
into `base/`: a fix is another update. `base/` changes only when a table is added (its CREATE TABLE), and a database
that already exists gets that table from the new base file, so a new base file may only create.

Every player's server runs a merged update at its next start, so the checks (`.github/scripts/check-sql.py`) refuse
`ATTACH`, `DETACH`, `VACUUM`, `PRAGMA` and `load_extension` anywhere, and in `auth` and `characters` a `DROP`, or a
`DELETE` or `UPDATE` without `WHERE`, unless the file holds the line `-- check-sql: destructive`. `base/` and those
two databases' updates are code-owned (the maintainer reviews them); the other updates are open to maintainers.

## The updater

`SqlUpdater` (GameServer.Storage) runs in each server when it opens a database: the files of `sql/base/<db>/`, then the
update folders of the database merged by file name, then its pending files. Each file runs once, in its own transaction
under SQLite's write lock, and is recorded in the database's `updates` table (name, SHA-1 of the text with LF line ends,
state `BASE`/`UPDATE`/`PENDING`, time, milliseconds). Auth and the Gateway can open the same file at once: a file is
checked again under the lock, so it runs once.

- A file that fails is rolled back and the server does not start (`SqlUpdateException`).
- A recorded file whose text changed stops the server: put it back and add a new update. While it is still pending,
  delete its row from `updates` (or the database) to run it again.
- A file with a recorded file's text under a new name, whose old file is gone, is the merge's rename: only its record is
  renamed.

The servers read `sql/` from their working directory (the repository root, or the release folder); `Game_SQL_DIR`
points elsewhere.

## client.db

The client tables are committed as SQL, the way cMaNGOS commits its DBC data: `sql/base/client/<Table>.sql` holds each
table's CREATE TABLE, and the rows come as update files in `sql/updates/client/`. The first import was one large file
per table (97 MB in all, the largest `Mods` at 17 MB). Our corrections are separate updates in
`sql/updates/client_fixes/` (pending: `pending_client_fixes/`), applied in name order with the client updates, so a
client patch never loses a fix.

`scripts/dev/client-sql.ps1` is the only writer of `base/client/` and `updates/client/`:

- `diff`, after a client patch and `extract-data.ps1`: builds the untouched client data (base + `updates/client/`,
  without the fixes) in a scratch database, compares it with `data/tables/*.json` and writes one dated update per
  changed table with only the differing rows (`INSERT`, `UPDATE` of the changed columns, `DELETE`). A new table gets its
  base file and a full data file; a table whose columns changed is dropped and recreated in its update. It names the fixes
  that touch a changed table, to check by hand. On an unchanged client it writes nothing.
- `full`: the first import; refuses when `base/client/` has tables.

A table has `_row INTEGER PRIMARY KEY` (the row's index: tables refer to each other's rows by index) and one column per
extracted column, typed `INTEGER`, `REAL`, `TEXT`, `BOOLEAN` (1/0) or `JSON` (lists, and columns that are always null,
as JSON text). A carriage return inside a string is written as `char(13)`, since the SQL files keep LF line ends.

The servers read a table through `ClientTables` (GameServer.Core), still by the path of its extracted file
(`data/tables/<Table>.json`): it returns that file's JSON, rebuilt from the rows and the column types, so the catalogues
parse what they parsed before. Any other path is read from disk. A rebuilt table is kept compressed in client.db's
`client_json_cache` table for the current set of applied updates (a rebuild of `Mods` takes about a second, a cached
read 0.4 s), and in memory for a few seconds for the catalogues that read the same table in a burst. The Gateway and Auth
update client.db before their first read; the first build takes about 16 s, a start with nothing new about 1.5 s.
`extract-data.ps1` still writes `data/tables/` for its other generators and for `client-sql.ps1 diff`; `live.ps1` and a
release run only its `-Servers` entries (`game-files.zip` and the local root hash).

The files those generators write that the servers read are in client.db too, in `client_files` (`file`, `part`, `ord`,
`value`): `area-transitions`, `room-points`, `object-types`, `item-types`, `monster-contacts`, `monster-takehit`,
`animation-contacts`, `projectile-clips`, `hideouts`, `vendor-stock`, `passive-skill-graphs`, `delve-paths`, the HNC mask
of `client-constants.json` and `stat-descriptions.txt` (`FILES` in `scripts/dev/client_sql.py`). A JSON file is one
row per top-level entry, and one per entry of a top-level list or object of more than 64 (`part`: the keys leading to
it, as a JSON array; `value`: its JSON); `stat-descriptions.txt` is one row per description block. `ClientTables`
serves `data/<file>` from them (same data and key order as the generator wrote, without the indentation), so a server
reads none of these from disk. `client-sql.ps1 diff` writes their changed rows (`<stamp>_file_<name>.sql`) alongside
the tables'; after a generator change, run `extract-data.ps1`, then `diff`, and commit both. The GGPK root hash stays
in the local `data/client-constants.json` (the Patch server's, refreshed by `live.ps1`): it describes the local
`Content.ggpk`. `data/game-files.zip`, the client's own level and object files, still comes only from the client.

## world.db

The game content that is ours rather than the client's: `system_settings`, `unique_weights` (+ `_overrides`),
`catalog_entries` (the unique, jewel, tattoo and jewel-art catalogues as one JSON payload per row), the `drop_*` tables,
`mod_stat_overrides`, `limit_overrides` and `item_templates` ([editable game data](editable-game-data.md),
[drop data](drop-data-schema.md)). The first updates hold what used to be seed arrays in code. The Gateway reads it at
startup and on `/reload`, which publishes new in-memory snapshots without a restart; a bad row (a payload that does not
parse, a setting out of range) fails the reload and the previous snapshots stay. Nothing writes world.db at run time.

Ruthless shares world.db. Where Ruthless content differs from the normal game, a table gets a `ruthless_<table>` twin
(or a `ruthless_` column, like `unique_weights.ruthless_weight`) when the first difference needs one, created and filled
from `sql/updates/ruthless/` (pending: `pending_ruthless/`), which applies with the world updates in name order.

## Migrating data/saves.db

Before the split, one `data/saves.db` held accounts, saves and the game-data overlay. The first server to open the
default player databases migrates it, so an install updated by hand or by a launcher keeps its saves; the others wait on
`data/saves.db.migrating`. `GameServer.Auth migrate-saves [saves.db]` (a release: `LocalExile migrate-saves`) does the
same on request. The migration copies it first (`saves.db.backup-<time>`, a `VACUUM INTO`), creates `auth.db` and `characters.db`,
copies the player tables over in one transaction, and renames `saves.db` to `saves.db.migrated`. The game-data tables are
left behind: the other databases are built from `sql/`. It refuses to write over an existing `auth.db` or
`characters.db`; a server then stops, since merging them is the owner's call.
