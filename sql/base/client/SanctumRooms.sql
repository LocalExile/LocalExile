-- Client table SanctumRooms (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SanctumRooms" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ArmFile" TEXT,
    "RoomType" INTEGER,
    "Script" TEXT,
    "Floor" INTEGER,
    "Area" INTEGER
);
