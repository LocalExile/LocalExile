-- Client table LabyrinthCraftOptions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "LabyrinthCraftOptions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "CraftFamily" INTEGER,
    "Text" TEXT,
    "Script" TEXT,
    "ScriptArgument" TEXT,
    "HASH16" INTEGER,
    "__col6" BOOLEAN,
    "Tier" INTEGER,
    "__col8" BOOLEAN,
    "Achievement" JSON,
    "__col10" BOOLEAN,
    "SoundEffect" INTEGER,
    "__col12" INTEGER
);
