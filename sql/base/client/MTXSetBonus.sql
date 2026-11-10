-- Client table MTXSetBonus (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MTXSetBonus" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ChatIcon" INTEGER,
    "Portal" JSON,
    "SetBonusStat" JSON,
    "Pets" JSON,
    "WeaponEffectStats" JSON
);
