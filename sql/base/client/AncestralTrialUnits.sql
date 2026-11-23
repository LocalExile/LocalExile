-- Client table AncestralTrialUnits (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AncestralTrialUnits" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Tribe" INTEGER,
    "Image" TEXT,
    "TotemImage" TEXT,
    "HASH16" INTEGER,
    "__col6" INTEGER,
    "Description" TEXT,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "Position" INTEGER
);
