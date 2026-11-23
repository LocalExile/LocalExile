-- Client table BestiaryRecipes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BestiaryRecipes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "BestiaryRecipeComponentKeys" JSON,
    "Notes" TEXT,
    "Category" INTEGER,
    "__col5" BOOLEAN,
    "Achievements" JSON,
    "__col7" BOOLEAN,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "GameMode" INTEGER,
    "FlaskMod" INTEGER
);
