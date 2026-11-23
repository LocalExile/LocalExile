-- Client table MapDeviceRecipes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapDeviceRecipes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "RecipeItems" JSON,
    "WorldArea" INTEGER,
    "MicrotransactionPortalVariation" INTEGER,
    "AreaLevel" INTEGER,
    "QuestFlag" INTEGER,
    "__col6" INTEGER,
    "__col7" BOOLEAN,
    "__col8" BOOLEAN,
    "__col9" BOOLEAN,
    "OpenAchievemnts" JSON,
    "__col11" BOOLEAN,
    "__col12" INTEGER,
    "Mods" JSON,
    "__col14" BOOLEAN,
    "__col15" JSON,
    "AtlasNode" INTEGER,
    "__col17" BOOLEAN
);
