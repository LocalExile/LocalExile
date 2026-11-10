-- Client table WeaponPassiveSkills (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "WeaponPassiveSkills" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Tier" INTEGER,
    "Mod" INTEGER,
    "NodeSpawnLocation" JSON,
    "Tags" JSON,
    "Icon" TEXT,
    "Type" INTEGER,
    "Achievements" JSON
);
