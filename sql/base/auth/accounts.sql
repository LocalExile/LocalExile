-- One row per account; the record (name, password verifier, ...) as JSON.
CREATE TABLE accounts (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    account TEXT NOT NULL
) STRICT;
