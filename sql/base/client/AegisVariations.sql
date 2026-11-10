-- Client table AegisVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AegisVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Name" TEXT,
    "DefendsAgainstPhysical" BOOLEAN,
    "DefendsAgainstFire" BOOLEAN,
    "DefendsAgainstCold" BOOLEAN,
    "DefendsAgainstLightning" BOOLEAN,
    "DefendsAgainstChaos" BOOLEAN,
    "ActiveBuff" INTEGER,
    "DepletedBuff" INTEGER,
    "__col8" INTEGER,
    "AnimatedEffect" INTEGER,
    "__col10" JSON
);
