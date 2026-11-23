-- The saves of deleted characters, set aside so a new character of the name starts over and the old one can be put back.
CREATE TABLE deleted_characters (
    name TEXT NOT NULL,
    deleted_utc TEXT NOT NULL,
    save TEXT NOT NULL
) STRICT;
