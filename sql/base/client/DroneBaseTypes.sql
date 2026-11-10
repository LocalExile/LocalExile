-- Client table DroneBaseTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DroneBaseTypes" (
    "_row" INTEGER PRIMARY KEY,
    "BaseType" INTEGER,
    "Type" INTEGER,
    "Charges" INTEGER,
    "Duration" INTEGER,
    "EnemyLimit" INTEGER,
    "Visual" INTEGER,
    "Empowerment" INTEGER,
    "UseAchievement" INTEGER,
    "CreatedViaPowerCore" BOOLEAN
);
