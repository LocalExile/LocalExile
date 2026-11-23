-- One save per character (progress, inventories, quests), as JSON.
CREATE TABLE characters (
    name TEXT PRIMARY KEY COLLATE NOCASE,
    save TEXT NOT NULL
) STRICT;
