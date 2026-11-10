-- One-time markers of the player databases (json_imported, accounts_imported, ...).
CREATE TABLE meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;
