-- Client table SynthesisGlobalMods (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SynthesisGlobalMods" (
    "_row" INTEGER PRIMARY KEY,
    "ModsKey" INTEGER,
    "Weight" INTEGER,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER
);
