-- Client table SanctumRoomTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SanctumRoomTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" BOOLEAN,
    "__col2" BOOLEAN,
    "QuestFlagEnter" INTEGER,
    "QuestFlagComplete" INTEGER,
    "__col5" BOOLEAN,
    "Icon" TEXT,
    "__col7" BOOLEAN,
    "Description" TEXT,
    "__col9" JSON,
    "Rooms" JSON,
    "__col11" TEXT,
    "__col12" BOOLEAN
);
