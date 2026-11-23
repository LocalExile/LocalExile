"""The checks on sql/ (Agents/docs/dev/databases.md), and the merge's rename of pending updates.

    check-sql.py --base <ref>     a pull request: the SQL rules on what changed since <ref>, then every database built
                                  from scratch the way the servers' updater builds it. Exit 1 on a problem.
    check-sql.py --rename-pending after a merge to main: each sql/updates/pending_<db>/<name>.sql becomes
                                  sql/updates/<db>/YYYY_MM_DD_HHMMSS_<name>.sql (git mv), in name order, after every
                                  merged file. The updater recognises a renamed file by its hash.

Run from the repository root. Python's sqlite3 runs the files; the order mirrors SqlUpdater.cs.
"""
import argparse
import datetime
import os
import re
import sqlite3
import subprocess
import sys
import tempfile

SQL = "sql"
# SqlUpdater.UpdateFolders: the update streams of each database, applied merged by file name.
DATABASES = {
    "auth": ["auth"],
    "characters": ["characters"],
    "client": ["client", "client_fixes"],
    "world": ["world", "ruthless"],
    "logs": ["logs"],
}
# Written straight to updates/ by their generator (scripts/dev/client-sql.ps1), already dated.
GENERATED = {"client"}
STAMP = re.compile(r"^\d{4}_\d{2}_\d{2}_\d{6}_")
# A pending file's leading number only orders it among the pending files; the merge drops it.
ORDER = re.compile(r"^(\d{4}_\d{2}_\d{2}_\d{6}_|\d+_)")
DATA = re.compile(r"^\s*(INSERT|UPDATE|DELETE|REPLACE)\b", re.IGNORECASE | re.MULTILINE)
# Strings, quoted names and comments, so a statement check never matches the data's own text.
NOT_CODE = re.compile(r"'(?:[^']|'')*'|\"(?:[^\"]|\"\")*\"|`[^`]*`|\[[^\]]*\]|--[^\n]*|/\*.*?\*/", re.DOTALL)
# What reaches past the file's own database: another file on disk (ATTACH, VACUUM INTO), native code, the
# connection's settings. No update needs them, generated ones included.
OUTSIDE = re.compile(r"\b(ATTACH|DETACH|VACUUM|PRAGMA|load_extension)\b", re.IGNORECASE)
# The databases of players' accounts and saves, which nothing regenerates: a statement that drops or empties
# a table there needs the maintainer's word in the file itself.
PLAYER_DATA = {"auth", "characters"}
DESTRUCTIVE_OK = "-- check-sql: destructive"


def files(folder):
    path = os.path.join(SQL, folder)
    return sorted(f"{folder}/{name}" for name in os.listdir(path) if name.endswith(".sql")) if os.path.isdir(path) else []


def scripts(database):
    """The files that build the database, in the updater's order."""
    streams = DATABASES[database]
    merged = sorted((f for s in streams for f in files(f"updates/{s}")), key=lambda f: (os.path.basename(f), f))
    pending = sorted(f for s in streams for f in files(f"updates/pending_{s}"))
    return files(f"base/{database}") + merged + pending


def text(name):
    with open(os.path.join(SQL, name), encoding="utf-8") as f:
        return f.read().replace("\r\n", "\n")


def style(name, problems):
    raw = open(os.path.join(SQL, name), "rb").read()
    try:
        content = raw.decode("utf-8")
    except UnicodeDecodeError as e:
        problems.append(f"{name}: not UTF-8 ({e})")
        return
    if content.startswith("\ufeff"):
        problems.append(f"{name}: starts with a byte order mark")
    if "\r" in content:
        problems.append(f"{name}: CR line ends (LF only; .gitattributes keeps sql/ LF)")
    if not content.endswith("\n"):
        problems.append(f"{name}: no newline at the end")
    # A generator's output keeps the client's own strings, tabs and trailing spaces included.
    generated = name.split("/")[:2] in [["updates", stream] for stream in GENERATED]
    for number, line in enumerate([] if generated else content.split("\n"), 1):
        if "\t" in line:
            problems.append(f"{name}:{number}: a tab (indent with spaces)")
        if line != line.rstrip():
            problems.append(f"{name}:{number}: trailing whitespace")
    code = [line for line in content.split("\n") if line.strip() and not line.strip().startswith("--")]
    if not code:
        problems.append(f"{name}: no statement")
    elif not code[-1].rstrip().endswith(";"):
        problems.append(f"{name}: the last statement does not end with ;")
    if name.startswith("base/") and DATA.search(content):
        problems.append(f"{name}: rows in base/ (base holds CREATE statements only; rows are an update)")
    statements(name, content, problems)


def statements(name, content, problems):
    code = NOT_CODE.sub(" ", content)
    for word in sorted({m.group(1).upper() for m in OUTSIDE.finditer(code)}):
        problems.append(f"{name}: {word} (an update reaches only its own database's tables)")
    database = name.split("/")[1].removeprefix("pending_")
    if database not in PLAYER_DATA or DESTRUCTIVE_OK in content:
        return
    for statement in code.split(";"):
        words = statement.split()
        first = words[0].upper() if words else ""
        if first == "DROP" or (first in ("DELETE", "UPDATE") and not re.search(r"\bWHERE\b", statement, re.IGNORECASE)):
            problems.append(f"{name}: {' '.join(words[:4])} ... drops or rewrites every row of player data; if that "
                            f"is intended, the maintainer adds the line '{DESTRUCTIVE_OK}' to the file")


def changes(base):
    """(status, path, new path) under sql/ since the merge base with <base>."""
    out = subprocess.run(["git", "diff", "--name-status", "-M", f"{base}...HEAD", "--", SQL],
                         check=True, capture_output=True, text=True).stdout
    for line in out.splitlines():
        parts = line.split("\t")
        yield parts[0][0], parts[1], parts[2] if len(parts) > 2 else parts[1]


def rules(base, problems):
    checked = []
    for status, old, new in changes(base):
        old, new = old[len(SQL) + 1:], new[len(SQL) + 1:]
        merged = lambda p: p.startswith("base/") or (p.startswith("updates/") and not p.split("/")[1].startswith("pending_"))
        if status in "MDR" and merged(old):
            problems.append(f"{old}: a merged file never changes, moves or goes; put the change in a new update "
                            f"(sql/updates/pending_<db>/)")
        if status in "ACRM" and os.path.exists(os.path.join(SQL, new)):
            if not new.endswith(".sql"):
                problems.append(f"{new}: only .sql files belong in sql/")
                continue
            parts = new.split("/")
            if parts[0] == "updates" and status != "M":
                stream = parts[1].removeprefix("pending_")
                if not any(stream in streams for streams in DATABASES.values()):
                    problems.append(f"{new}: no database reads sql/updates/{parts[1]}/")
                elif not parts[1].startswith("pending_") and stream not in GENERATED:
                    problems.append(f"{new}: a new update goes in sql/updates/pending_{stream}/; the merge names it")
            elif parts[0] == "base" and parts[1] not in DATABASES:
                problems.append(f"{new}: no database {parts[1]}")
            checked.append(new)
    for name in checked:
        style(name, problems)
    return len(checked)


def build(problems):
    with tempfile.TemporaryDirectory() as folder:
        for database in DATABASES:
            if not os.path.isdir(os.path.join(SQL, "base", database)):
                continue
            path = os.path.join(folder, database + ".db")
            db = sqlite3.connect(path, isolation_level=None)
            count = 0
            for name in scripts(database):
                # One transaction per file, as the updater runs it: a failed file leaves nothing behind.
                try:
                    db.executescript("BEGIN;\n" + text(name) + "\nCOMMIT;")
                    count += 1
                except sqlite3.Error as e:
                    if db.in_transaction:
                        db.execute("ROLLBACK")
                    problems.append(f"{name}: {e} (building {database}.db)")
                    break
            tables = db.execute("SELECT count(*) FROM sqlite_master WHERE type = 'table'").fetchone()[0]
            db.close()
            print(f"{database}.db: {count} file(s), {tables} table(s)")


def rename_pending():
    now = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0)
    renamed = 0
    for streams in DATABASES.values():
        for stream in streams:
            pending = files(f"updates/pending_{stream}")
            if not pending:
                continue
            merged = [os.path.basename(f) for s in streams for f in files(f"updates/{s}")]
            stamp = now
            last = max((m[:17] for m in merged if STAMP.match(m)), default="")
            for name in pending:
                while stamp.strftime("%Y_%m_%d_%H%M%S") <= last:
                    stamp += datetime.timedelta(seconds=1)
                base = ORDER.sub("", os.path.basename(name))
                target = f"updates/{stream}/{stamp.strftime('%Y_%m_%d_%H%M%S')}_{base}"
                os.makedirs(os.path.join(SQL, "updates", stream), exist_ok=True)
                subprocess.run(["git", "mv", os.path.join(SQL, name), os.path.join(SQL, target)], check=True)
                print(f"{name} -> {target}")
                last = stamp.strftime("%Y_%m_%d_%H%M%S")
                renamed += 1
    print(f"{renamed} pending file(s) named")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", help="the ref a pull request merges into")
    parser.add_argument("--rename-pending", action="store_true")
    args = parser.parse_args()
    if args.rename_pending:
        rename_pending()
        return 0
    problems = []
    if args.base:
        print(f"{rules(args.base, problems)} changed SQL file(s) checked")
    build(problems)
    for problem in problems:
        print(f"::error::{problem}" if os.environ.get("GITHUB_ACTIONS") else problem)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
