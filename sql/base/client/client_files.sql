-- The generated files (scripts/dev/client_sql.py FILES), one row per entry; ClientTables rebuilds data/<file>.
-- Its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE client_files (
    file TEXT NOT NULL,
    part TEXT NOT NULL,
    ord INTEGER NOT NULL,
    value TEXT NOT NULL,
    PRIMARY KEY (file, part)
);
