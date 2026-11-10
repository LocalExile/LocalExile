-- One account stash header (tabs, gold, last tab) per account name, as JSON.
CREATE TABLE stashes (
    account TEXT PRIMARY KEY COLLATE NOCASE,
    save TEXT NOT NULL
) STRICT;
