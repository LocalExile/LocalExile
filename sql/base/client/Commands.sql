-- Client table Commands (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Commands" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Command" TEXT,
    "SendsGenericCommandPacket" BOOLEAN,
    "EnglishCommand" TEXT,
    "Description" TEXT,
    "__col5" BOOLEAN
);
