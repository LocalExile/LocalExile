-- Client table Music (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Music" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "SoundFile" TEXT,
    "BankFile" TEXT,
    "HASH16" INTEGER,
    "IsAvailableInHideout" BOOLEAN,
    "Name" TEXT,
    "__col6" TEXT,
    "MusicCategories" JSON,
    "__col8" BOOLEAN,
    "__col9" INTEGER
);
