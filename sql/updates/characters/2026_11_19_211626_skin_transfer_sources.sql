-- A native skin transfer retains one complete donor document, including its own socketed items and modifiers.
-- Keeping it separate from the target's columns leaves both items' ordinary properties untouched.
CREATE TABLE item_skin_transfers (
    target INTEGER PRIMARY KEY REFERENCES items (id),
    source TEXT NOT NULL CHECK (json_valid(source))
) STRICT;
