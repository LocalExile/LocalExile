-- Client table RitualSetKillAchievements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "RitualSetKillAchievements" (
    "_row" INTEGER PRIMARY KEY,
    "Achievement" INTEGER,
    "KillBosses" JSON
);
