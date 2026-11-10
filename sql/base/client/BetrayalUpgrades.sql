-- Client table BetrayalUpgrades (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BetrayalUpgrades" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Description" TEXT,
    "ModsKey" JSON,
    "ArtFile" TEXT,
    "BetrayalUpgradeSlotsKey" INTEGER,
    "__col6" JSON,
    "ItemVisualIdentityKey0" INTEGER,
    "ItemVisualIdentityKey1" INTEGER,
    "GrantedEffectsKey" JSON,
    "__col10" INTEGER,
    "ItemClassesKey" INTEGER
);
