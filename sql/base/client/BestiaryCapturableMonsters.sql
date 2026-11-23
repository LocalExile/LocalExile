-- Client table BestiaryCapturableMonsters (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BestiaryCapturableMonsters" (
    "_row" INTEGER PRIMARY KEY,
    "MonsterVarietiesKey" INTEGER,
    "BestiaryGroupsKey" INTEGER,
    "BestiaryEncountersKey" INTEGER,
    "__col3" BOOLEAN,
    "IconSmall" TEXT,
    "Icon" TEXT,
    "Boss_MonsterVarietiesKey" INTEGER,
    "BestiaryGenusKey" INTEGER,
    "__col8" BOOLEAN,
    "BestiaryCapturableMonstersKey" INTEGER,
    "IsDisabled" BOOLEAN,
    "__col11" INTEGER,
    "__col12" BOOLEAN
);
