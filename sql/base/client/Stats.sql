-- Client table Stats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Stats" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "IsPublic" BOOLEAN,
    "IsLocal" BOOLEAN,
    "IsWeaponLocal" BOOLEAN,
    "Semantics" INTEGER,
    "IsParty" BOOLEAN,
    "IsVirtual" BOOLEAN,
    "MainHandAlias_StatsKey" INTEGER,
    "OffHandAlias_StatsKey" INTEGER,
    "IsProxied" BOOLEAN,
    "HASH32" INTEGER,
    "BelongsActiveSkillsKey" JSON,
    "Category" INTEGER,
    "__col13" BOOLEAN,
    "IsApplyDamageFunctionStat" BOOLEAN,
    "IsScalable" BOOLEAN,
    "ContextFlags" JSON,
    "DotFlag" JSON
);
