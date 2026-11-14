-- Client table LabyrinthSecretEffects (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "LabyrinthSecretEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MonsterVarietiesKey" INTEGER,
    "Buff_BuffDefinitionsKey" INTEGER,
    "Buff_StatValues" JSON,
    "OTFile" TEXT,
    "Buff_StatValuesRuthless" JSON
);
