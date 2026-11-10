-- The character list Auth shows at login; the record as JSON. account_id is an accounts.id of auth.db.
CREATE TABLE account_characters (
    id TEXT PRIMARY KEY,
    account_id TEXT NOT NULL,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    created_utc TEXT NOT NULL,
    character TEXT NOT NULL
) STRICT;
