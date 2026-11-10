-- Client table MonsterTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MonsterTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Accuracy" INTEGER,
    "IsPlayerMinion" BOOLEAN,
    "Armour" INTEGER,
    "Evasion" INTEGER,
    "EnergyShield" INTEGER,
    "DamageSpread" INTEGER,
    "Resistances" INTEGER,
    "AltLife1" BOOLEAN,
    "AltLife2" BOOLEAN,
    "BaseDamageIgnoresAttackSpeed" BOOLEAN
);
