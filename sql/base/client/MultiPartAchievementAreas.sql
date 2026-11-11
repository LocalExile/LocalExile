-- Client table MultiPartAchievementAreas (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MultiPartAchievementAreas" (
    "_row" INTEGER PRIMARY KEY,
    "Achievement" INTEGER,
    "Area" JSON,
    "MinimumAreaLevel" INTEGER
);
