"""The client tables as SQL (Agents/docs/dev/databases.md, "client.db"). Run through scripts/dev/client-sql.ps1.

  full   the first import: sql/base/client/<Table>.sql (CREATE TABLE, no rows) and one dated data file per table in
         sql/updates/client/. Refuses when sql/base/client/ already has files.
  diff   after a client patch: builds the untouched client data (base + sql/updates/client/, without the fixes) in a
         scratch database, compares it with data/tables/*.json (extract-data.ps1's output for the new client) and writes
         one dated update per changed table with only the differing rows (INSERT / UPDATE of the changed columns /
         DELETE). A new table gets its base file and a full data file; a table whose columns changed is recreated. It
         lists the fixes (sql/updates/client_fixes/) that touch a changed table, to check by hand.

Column types (the server rebuilds the JSON rows from them, GameServer.Core ClientTables):
  _row INTEGER PRIMARY KEY  the row's index in the table (other tables refer to rows by index)
  INTEGER, REAL, TEXT       numbers and strings
  BOOLEAN                   true/false as 1/0
  JSON                      lists, and columns that are always null, as JSON text
"""
import argparse
import json
import math
import os
import sqlite3
import sys
import tempfile
import unicodedata
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TABLES = os.path.join(ROOT, "data", "tables")
SQL = os.path.join(ROOT, "sql")
BASE = os.path.join(SQL, "base", "client")
UPDATES = os.path.join(SQL, "updates", "client")
FIXES = os.path.join(SQL, "updates", "client_fixes")
BATCH = 500  # rows per INSERT statement
CLIENT = "3.29.3c"


def q(name):
    return '"' + name.replace('"', '""') + '"'


def column_type(values):
    kinds = {type(v) for v in values if v is not None}
    if not kinds:
        return "JSON"
    if kinds == {bool}:
        return "BOOLEAN"
    if kinds <= {int}:
        return "INTEGER"
    if kinds <= {int, float}:
        return "REAL"
    if kinds == {str}:
        return "TEXT"
    return "JSON"


def unseen(char):
    """A character a reviewer would not see (the rule of .github/scripts/scan-docs.py), or a carriage return, which git
    and the updater would drop from a SQL file kept with LF line ends."""
    code = ord(char)
    if char in "\t\n":
        return False
    return (unicodedata.category(char) in ("Cf", "Cc", "Zl", "Zp", "Co", "Cn")
            or 0xFE00 <= code <= 0xFE0E or 0xE0100 <= code <= 0xE01EF
            or code in (0x115F, 0x1160, 0x17B4, 0x17B5, 0x2800, 0x3164, 0xFFA0))


def text(value):
    """A SQL string literal; characters a reviewer would not see are written as char(n)."""
    return "'" + "".join(f"' || char({ord(c)}) || '" if unseen(c) else c for c in value.replace("'", "''")) + "'"


def literal(value, kind):
    if value is None:
        return "NULL"
    if kind == "JSON":
        return text(json.dumps(value, ensure_ascii=False, separators=(",", ":")))
    if kind == "BOOLEAN":
        return "1" if value else "0"
    if kind == "INTEGER":
        return str(value)
    if kind == "REAL":
        if isinstance(value, float) and not math.isfinite(value):
            raise ValueError(f"{value} is not a finite number")
        return repr(float(value)) if isinstance(value, float) else str(value)
    return text(value)


def read_table(name):
    with open(os.path.join(TABLES, name + ".json"), encoding="utf-8") as f:
        rows = json.load(f)
    columns = list(rows[0].keys()) if rows else []
    types = {c: column_type(r.get(c) for r in rows) for c in columns}
    return rows, columns, types


def create_sql(name, columns, types):
    lines = [f"    {q('_row')} INTEGER PRIMARY KEY"] + [f"    {q(c)} {types[c]}" for c in columns]
    return f"CREATE TABLE {q(name)} (\n" + ",\n".join(lines) + "\n);\n"


def insert_sql(name, columns, types, rows):
    """rows: (index, row) pairs."""
    out = []
    head = f"INSERT INTO {q(name)} ({', '.join(q(c) for c in ['_row'] + columns)}) VALUES\n"
    for start in range(0, len(rows), BATCH):
        chunk = rows[start:start + BATCH]
        values = ",\n".join("(" + ", ".join([str(i)] + [literal(r.get(c), types[c]) for c in columns]) + ")" for i, r in chunk)
        out.append(head + values + ";\n")
    return "".join(out)


def stamp():
    return datetime.now(timezone.utc).strftime("%Y_%m_%d_%H%M%S")


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def table_names():
    if not os.path.isdir(TABLES):
        sys.exit(f"{TABLES} is missing: run scripts/dev/extract-data.ps1 first")
    return sorted(f[:-5] for f in os.listdir(TABLES) if f.endswith(".json"))


def full(_):
    if os.path.isdir(BASE) and any(f.endswith(".sql") for f in os.listdir(BASE)):
        sys.exit(f"{BASE} already has tables: use 'diff' for a client patch")
    when = stamp()
    total = 0
    for name in table_names():
        rows, columns, types = read_table(name)
        write(os.path.join(BASE, name + ".sql"),
              f"-- Client table {name} ({CLIENT}); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.\n"
              + create_sql(name, columns, types))
        write(os.path.join(UPDATES, f"{when}_{name}.sql"),
              f"-- Client table {name}: all {len(rows)} rows of the {CLIENT} client. Written by scripts/dev/client-sql.ps1.\n"
              + insert_sql(name, columns, types, list(enumerate(rows))))
        total += len(rows)
    print(f"client-sql: {len(table_names())} tables, {total} rows written ({when})")


def build_original(db):
    """base/client + updates/client in the updater's order (base first, then updates by name); not the fixes."""
    for folder in (BASE, UPDATES):
        if not os.path.isdir(folder):
            continue
        for f in sorted(os.listdir(folder)):
            if f.endswith(".sql"):
                with open(os.path.join(folder, f), encoding="utf-8") as h:
                    db.executescript(h.read())


def decode(value, kind):
    if value is None:
        return None
    if kind == "JSON":
        return json.loads(value)
    if kind == "BOOLEAN":
        return bool(value)
    if kind == "REAL":
        return float(value)
    return value


def same(a, b, kind):
    if kind == "REAL" and a is not None and b is not None:
        return float(a) == float(b)
    return a == b


def diff(_):
    when = stamp()
    with tempfile.TemporaryDirectory() as scratch:
        db = sqlite3.connect(os.path.join(scratch, "original.db"))
        build_original(db)
        existing = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type = 'table'")}
        names = table_names()
        changed = []
        for name in names:
            rows, columns, types = read_table(name)
            if name not in existing:
                write(os.path.join(BASE, name + ".sql"),
                      f"-- Client table {name} (new in {CLIENT}); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.\n"
                      + create_sql(name, columns, types))
                write(os.path.join(UPDATES, f"{when}_{name}.sql"),
                      f"-- Client table {name}: new, {len(rows)} rows. Written by scripts/dev/client-sql.ps1.\n"
                      + insert_sql(name, columns, types, list(enumerate(rows))))
                changed.append((name, f"new, {len(rows)} rows"))
                continue
            info = db.execute(f"PRAGMA table_info({q(name)})").fetchall()
            had = [(r[1], r[2]) for r in info if r[1] != "_row"]
            if had != [(c, types[c]) for c in columns]:
                write(os.path.join(UPDATES, f"{when}_{name}.sql"),
                      f"-- Client table {name}: its columns changed in {CLIENT}; recreated with all {len(rows)} rows. "
                      "Written by scripts/dev/client-sql.ps1.\n"
                      + f"DROP TABLE {q(name)};\n" + create_sql(name, columns, types)
                      + insert_sql(name, columns, types, list(enumerate(rows))))
                changed.append((name, f"columns changed, recreated ({len(rows)} rows)"))
                continue
            old = {r[0]: r[1:] for r in db.execute(f"SELECT * FROM {q(name)} ORDER BY {q('_row')}")}
            inserts, updates, deletes = [], [], []
            for i, row in enumerate(rows):
                if i not in old:
                    inserts.append((i, row))
                    continue
                sets = [f"{q(c)} = {literal(row.get(c), types[c])}" for c, v in zip(columns, old[i])
                        if not same(decode(v, types[c]), row.get(c), types[c])]
                if sets:
                    updates.append(f"UPDATE {q(name)} SET {', '.join(sets)} WHERE {q('_row')} = {i};\n")
            deletes = [i for i in old if i >= len(rows)]
            if not (inserts or updates or deletes):
                continue
            text = f"-- Client table {name}: {len(inserts)} new, {len(updates)} changed, {len(deletes)} removed row(s) in {CLIENT}. " \
                   "Written by scripts/dev/client-sql.ps1.\n"
            if deletes:
                text += f"DELETE FROM {q(name)} WHERE {q('_row')} >= {len(rows)};\n"
            text += "".join(updates) + insert_sql(name, columns, types, inserts)
            write(os.path.join(UPDATES, f"{when}_{name}.sql"), text)
            changed.append((name, f"{len(inserts)} new, {len(updates)} changed, {len(deletes)} removed"))
        for name in sorted(existing - set(names)):
            write(os.path.join(UPDATES, f"{when}_{name}.sql"),
                  f"-- Client table {name}: not in the {CLIENT} client any more. Written by scripts/dev/client-sql.ps1.\n"
                  + f"DROP TABLE {q(name)};\n")
            changed.append((name, "removed"))
        db.close()
    for name, what in changed:
        print(f"client-sql: {name}: {what}")
    print(f"client-sql: {len(changed)} table(s) changed ({when})" if changed else "client-sql: the client tables are unchanged")
    if changed and os.path.isdir(FIXES):
        touched = {n for n, _ in changed}
        for f in sorted(os.listdir(FIXES)):
            with open(os.path.join(FIXES, f), encoding="utf-8") as h:
                text = h.read()
            hit = sorted(n for n in touched if q(n) in text or f" {n} " in text)
            if hit:
                print(f"client-sql: check the fix {f}: it touches {', '.join(hit)}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mode", choices=["full", "diff"])
    args = parser.parse_args()
    {"full": full, "diff": diff}[args.mode](args)


if __name__ == "__main__":
    main()
