-- Account-level friend invitations (owner -> friend) and acknowledgements (both rows). Ids are accounts.id of auth.db.
CREATE TABLE account_friends (
    owner TEXT NOT NULL,
    friend TEXT NOT NULL,
    PRIMARY KEY (owner, friend),
    CHECK (owner <> friend)
) STRICT;
