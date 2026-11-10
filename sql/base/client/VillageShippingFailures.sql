-- Client table VillageShippingFailures (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "VillageShippingFailures" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Text" TEXT,
    "Resource" JSON,
    "ResourceLostPercent" INTEGER,
    "PortBonusLostPercent" INTEGER,
    "__col5" INTEGER,
    "CrewMembersLost" INTEGER,
    "__col7" INTEGER,
    "SubText" TEXT,
    "__col9" INTEGER
);
