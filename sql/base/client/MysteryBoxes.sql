-- Client table MysteryBoxes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MysteryBoxes" (
    "_row" INTEGER PRIMARY KEY,
    "MtxTypesKey" INTEGER,
    "BK2File" TEXT,
    "BoxId" TEXT,
    "BundleId" TEXT,
    "__col4" BOOLEAN,
    "Logo" TEXT,
    "Gradient" TEXT
);
