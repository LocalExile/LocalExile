-- Client table WeaponTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "WeaponTypes" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "Critical" INTEGER,
    "Speed" INTEGER,
    "DamageMin" INTEGER,
    "DamageMax" INTEGER,
    "RangeMax" INTEGER,
    "__col6" INTEGER
);
