# Databases

How the servers store data, and how a change to it is made: SQLite files split by owner, built from SQL kept in git
(the TrinityCore/AzerothCore/cMaNGOS model, [#734](https://github.com/LocalExile/LocalExile/issues/734)).

## The files

Every database is a SQLite file under `data/` (git-ignored), created and brought up to date by the server that opens it.

| File | Holds | Opened by | Override |
|---|---|---|---|
| `auth.db` | accounts | Auth, Host, Gateway (attached to `characters.db`) | `Game_AUTH_DB` |
| `characters.db` | character saves, stashes, the login character list, friends, deleted characters | Gateway, Auth, Host | `Game_CHARACTERS_DB` |

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

A change to a database, rows or schema, is a new file in `sql/updates/pending_<db>/`. After the merge it is renamed to
`YYYY_MM_DD_HHMMSS_<name>.sql` and moved to `sql/updates/<db>/`. A merged file never changes and nothing is ever squashed
into `base/`: a fix is another update. `base/` changes only when a table is added (its CREATE TABLE), and a database
that already exists gets that table from the new base file, so a new base file may only create.

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

## Migrating data/saves.db

Before the split, one `data/saves.db` held accounts, saves and the game-data overlay. A server refuses to open the
default player databases while it is there. `GameServer.Auth migrate-saves [saves.db]` (a release: `LocalExile
migrate-saves`) copies it first (`saves.db.backup-<time>`, a `VACUUM INTO`), creates `auth.db` and `characters.db`,
copies the player tables over in one transaction, and renames `saves.db` to `saves.db.migrated`. The game-data tables are
left behind: the other databases are built from `sql/`. It refuses to write over an existing `auth.db` or
`characters.db`.
