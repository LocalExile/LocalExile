-- Client table Words (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Words" (
    "_row" INTEGER PRIMARY KEY,
    "Wordlist" INTEGER,
    "Text" TEXT,
    "SpawnWeight_Tags" JSON,
    "SpawnWeight_Values" JSON,
    "HASH32" INTEGER,
    "Text2" TEXT,
    "Inflection" TEXT
);
