-- Client table MapConnections (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapConnections" (
    "_row" INTEGER PRIMARY KEY,
    "MapPinsKey0" INTEGER,
    "MapPinsKey1" INTEGER,
    "QuestCompleteFlag" INTEGER,
    "RestrictedAreaText" TEXT,
    "QuestGainedFlag" INTEGER,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "__col7" JSON
);
