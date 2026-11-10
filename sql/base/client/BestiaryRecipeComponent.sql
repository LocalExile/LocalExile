-- Client table BestiaryRecipeComponent (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BestiaryRecipeComponent" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MinLevel" INTEGER,
    "BestiaryFamiliesKey" INTEGER,
    "BestiaryGroupsKey" INTEGER,
    "ModsKey" INTEGER,
    "BestiaryCapturableMonstersKey" INTEGER,
    "BeastRarity" INTEGER,
    "BestiaryGenusKey" INTEGER
);
