-- Client table ChestEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ChestEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Normal_EPKFile" TEXT,
    "Normal_Closed_AOFile" TEXT,
    "Normal_Open_AOFile" TEXT,
    "Magic_EPKFile" TEXT,
    "Unique_EPKFile" TEXT,
    "Rare_EPKFile" TEXT,
    "Magic_Closed_AOFile" TEXT,
    "Unique_Closed_AOFile" TEXT,
    "Rare_Closed_AOFile" TEXT,
    "Magic_Open_AOFile" TEXT,
    "Unique_Open_AOFile" TEXT,
    "Rare_Open_AOFile" TEXT
);
