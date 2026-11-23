-- Client table PantheonSouls (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PantheonSouls" (
    "_row" INTEGER PRIMARY KEY,
    "WorldArea" INTEGER,
    "CapturedVessel" INTEGER,
    "QuestFlagUpgrade" INTEGER,
    "CapturedMonster" JSON,
    "PanelLayout" INTEGER,
    "CapturedMonsterDescription" TEXT,
    "__col6" INTEGER,
    "QuestFlagDowngrade" INTEGER
);
