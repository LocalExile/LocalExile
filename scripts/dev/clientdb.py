"""The client tables in data/client.db (Agents/docs/dev/databases.md, "Reading game data") for the Python tools.

    import clientdb
    rows = clientdb.table("WorldAreas")       # [{"Id": "1_1_1", "TopologiesKeys": [12], ...}, ...] in row order

A row is a dict without `_row` (its index in the list is the row): JSON columns (lists) come back parsed, BOOLEAN
columns as bool. `Game_CLIENT_DB` names another database. Read-only; tables are cached per process.
"""
import json
import os
import sqlite3

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
_tables = {}
_dbs = {}


def path():
    return os.environ.get("Game_CLIENT_DB") or os.path.join(ROOT, "data", "client.db")


def connect(db=None):
    """A read-only connection to client.db, or to the database at `db` (FileNotFoundError without it)."""
    p = os.path.abspath(str(db or path()))
    if p not in _dbs:
        if not os.path.exists(p):
            raise FileNotFoundError(f"{p}: no client.db (start the servers once with scripts/dev/live.ps1, which builds it)")
        _dbs[p] = sqlite3.connect("file:" + p.replace("\\", "/") + "?mode=ro", uri=True)
    return _dbs[p]


def exists(db=None):
    return os.path.exists(str(db or path()))


def names(db=None):
    """Every client table's name (the database's own tables, all lower case, are left out)."""
    return [n for (n,) in connect(db).execute("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")
            if n != n.lower()]


def has(name, db=None):
    return connect(db).execute("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?", (name,)).fetchone() is not None


def table(name, db=None):
    """The table's rows in order, as dicts (KeyError without the table)."""
    key = (os.path.abspath(str(db or path())), name)
    if key not in _tables:
        conn = connect(db)
        if not has(name, db):
            raise KeyError(f"client.db has no table {name}")
        info = conn.execute(f'PRAGMA table_info("{name}")').fetchall()
        kinds = [(c[1], (c[2] or "").upper()) for c in info]
        rows = []
        for values in conn.execute(f'SELECT * FROM "{name}" ORDER BY _row'):
            row = {}
            for (col, kind), v in zip(kinds, values):
                if col == "_row":
                    continue
                if v is not None and kind == "JSON":
                    v = json.loads(v)
                elif v is not None and kind == "BOOLEAN":
                    v = bool(v)
                row[col] = v
            rows.append(row)
        _tables[key] = rows
    return _tables[key]



def close(db=None):
    """Closes the connection to a database and drops its cached tables (a test's temporary database)."""
    p = os.path.abspath(str(db or path()))
    if p in _dbs:
        _dbs.pop(p).close()
    for key in [k for k in _tables if k[0] == p]:
        del _tables[key]
