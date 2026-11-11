-- Client table ArmourTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ArmourTypes" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "ArmourMin" INTEGER,
    "ArmourMax" INTEGER,
    "EvasionMin" INTEGER,
    "EvasionMax" INTEGER,
    "EnergyShieldMin" INTEGER,
    "EnergyShieldMax" INTEGER,
    "IncreasedMovementSpeed" INTEGER,
    "WardMin" INTEGER,
    "WardMax" INTEGER,
    "__col10" INTEGER,
    "__col11" INTEGER
);
