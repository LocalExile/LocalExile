-- Client table MercenarySkills (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MercenarySkills" (
    "_row" INTEGER PRIMARY KEY,
    "GrantedEffect" INTEGER,
    "SupportCount" INTEGER,
    "PossibleSupports" JSON,
    "EncounterGrantedEffect" INTEGER,
    "Name" TEXT,
    "Description" TEXT,
    "RequiredLevel" INTEGER,
    "__col7" INTEGER,
    "SkillFamily" INTEGER,
    "HASH16" INTEGER,
    "HouseIcon" TEXT
);
