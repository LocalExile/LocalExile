-- Client table PassiveSkillMasteryGroups (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PassiveSkillMasteryGroups" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MasteryEffects" JSON,
    "InactiveIcon" TEXT,
    "ActiveIcon" TEXT,
    "ActiveEffectImage" TEXT,
    "__col5" BOOLEAN,
    "SoundEffect" INTEGER,
    "MasteryCountStat" INTEGER
);
