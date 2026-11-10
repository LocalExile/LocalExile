-- Client table Realms (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Realms" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Server" JSON,
    "IsEnabled" BOOLEAN,
    "Server2" JSON,
    "ShortName" TEXT,
    "__col6" JSON,
    "__col7" INTEGER,
    "__col8" INTEGER,
    "IsGammaRealm" BOOLEAN,
    "SpeedtestUrl" JSON
);
