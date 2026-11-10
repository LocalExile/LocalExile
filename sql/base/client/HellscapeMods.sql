-- Client table HellscapeMods (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HellscapeMods" (
    "_row" INTEGER PRIMARY KEY,
    "Mod" INTEGER,
    "TiersWhitelist" JSON,
    "TransformAchievement" INTEGER,
    "ModFamilies" JSON
);
