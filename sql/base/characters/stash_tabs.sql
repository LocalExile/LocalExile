-- One stash tab's items; the stash's own row (stashes) holds its header.
CREATE TABLE stash_tabs (
    account TEXT NOT NULL COLLATE NOCASE,
    tab INTEGER NOT NULL,
    items TEXT NOT NULL,
    PRIMARY KEY (account, tab)
) STRICT;
