-- Client table PassiveSkillOverrides (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PassiveSkillOverrides" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "NodeIcon" TEXT,
    "Stats" JSON,
    "StatValues" JSON,
    "HASH16" INTEGER,
    "PassiveBG" TEXT,
    "Effect" INTEGER,
    "Type" INTEGER,
    "Limit" INTEGER,
    "RequiresAdjacent" INTEGER,
    "MaxAdjacent" INTEGER,
    "AllocatedPassiveSkill" INTEGER,
    "TattooBlockingPassive" JSON
);
