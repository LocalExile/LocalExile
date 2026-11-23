-- Client table DefaultMonsterStats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DefaultMonsterStats" (
    "_row" INTEGER PRIMARY KEY,
    "DisplayLevel" TEXT,
    "Damage" REAL,
    "Evasion" INTEGER,
    "Accuracy" INTEGER,
    "MonsterLife" INTEGER,
    "Experience" INTEGER,
    "MinionLife" INTEGER,
    "Armour" INTEGER,
    "Difficulty" INTEGER,
    "MinionDamage" REAL,
    "AltLife1" INTEGER,
    "AltDamage1" REAL,
    "AltDamage2" REAL,
    "AltLife2" INTEGER,
    "EvasiveEvasion" INTEGER,
    "AilmentThreshold" INTEGER,
    "MonsterPhysConversionMulti" INTEGER
);
