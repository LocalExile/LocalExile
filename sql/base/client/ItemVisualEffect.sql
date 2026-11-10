-- Client table ItemVisualEffect (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ItemVisualEffect" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "DaggerEPKFile" TEXT,
    "BowEPKFile" TEXT,
    "OneHandedMaceEPKFile" TEXT,
    "OneHandedSwordEPKFile" TEXT,
    "__col5" TEXT,
    "TwoHandedSwordEPKFile" TEXT,
    "TwoHandedStaffEPKFile" TEXT,
    "HASH16" INTEGER,
    "TwoHandedMaceEPKFile" TEXT,
    "OneHandedAxeEPKFile" TEXT,
    "TwoHandedAxeEPKFile" TEXT,
    "ClawEPKFile" TEXT,
    "PETFile" TEXT,
    "Shield" JSON,
    "__col15" BOOLEAN,
    "__col16" TEXT
);
