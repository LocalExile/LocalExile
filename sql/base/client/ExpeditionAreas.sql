-- Client table ExpeditionAreas (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ExpeditionAreas" (
    "_row" INTEGER PRIMARY KEY,
    "Area" INTEGER,
    "PosX" INTEGER,
    "PosY" INTEGER,
    "Tags" JSON,
    "__col4" JSON,
    "__col5" BOOLEAN,
    "TextAudio" INTEGER,
    "CompletionAchievements" JSON
);
