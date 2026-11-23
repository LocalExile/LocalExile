-- Client table MonsterResistances (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MonsterResistances" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "FireNormal" INTEGER,
    "ColdNormal" INTEGER,
    "LightningNormal" INTEGER,
    "ChaosNormal" INTEGER,
    "FireCruel" INTEGER,
    "ColdCruel" INTEGER,
    "LightningCruel" INTEGER,
    "ChaosCruel" INTEGER,
    "FireMerciless" INTEGER,
    "ColdMerciless" INTEGER,
    "LightningMerciless" INTEGER,
    "ChaosMerciless" INTEGER
);
