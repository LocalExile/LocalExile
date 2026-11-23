-- Client table PassiveSkillMasteryEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PassiveSkillMasteryEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "HASH16" INTEGER,
    "Stats" JSON,
    "Stat1Value" INTEGER,
    "Stat2Value" INTEGER,
    "Stat3Value" INTEGER,
    "AchievementItem" INTEGER,
    "GrantedEffect" INTEGER
);
