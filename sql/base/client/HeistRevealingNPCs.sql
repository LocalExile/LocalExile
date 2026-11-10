-- Client table HeistRevealingNPCs (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistRevealingNPCs" (
    "_row" INTEGER PRIMARY KEY,
    "NPCsKey" INTEGER,
    "PortraitFile" TEXT,
    "NPCAudioKey" JSON,
    "__col3" INTEGER
);
