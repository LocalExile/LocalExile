-- Client table LabyrinthTrinkets (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "LabyrinthTrinkets" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "LabyrinthSecretsKey" JSON,
    "Buff_BuffDefinitionsKey" INTEGER,
    "Buff_StatValues" JSON
);
