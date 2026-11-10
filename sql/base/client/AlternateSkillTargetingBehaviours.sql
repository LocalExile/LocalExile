-- Client table AlternateSkillTargetingBehaviours (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AlternateSkillTargetingBehaviours" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MaxDistance" INTEGER,
    "ClientStrings" INTEGER,
    "MaxMineTargetingDistance" INTEGER,
    "MaxTrapTargetingDistance" INTEGER,
    "MaxTotemTargetingDistance" INTEGER,
    "TargetingFlags" JSON
);
