-- Client table ArchnemesisModComboAchievements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ArchnemesisModComboAchievements" (
    "_row" INTEGER PRIMARY KEY,
    "Achievement" INTEGER,
    "Mods" JSON
);
