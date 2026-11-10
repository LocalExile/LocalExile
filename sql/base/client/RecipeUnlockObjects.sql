-- Client table RecipeUnlockObjects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "RecipeUnlockObjects" (
    "_row" INTEGER PRIMARY KEY,
    "WorldAreasKey" INTEGER,
    "InheritsFrom" TEXT,
    "RecipeId" INTEGER
);
