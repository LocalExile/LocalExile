-- Client table ExpeditionRelicMods (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ExpeditionRelicMods" (
    "_row" INTEGER PRIMARY KEY,
    "Mod" INTEGER,
    "Categories" JSON,
    "DestroyAchievements" JSON
);
