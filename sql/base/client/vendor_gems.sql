-- Generated table vendor_gems (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "vendor_gems" (
    "_row" INTEGER PRIMARY KEY,
    "Vendor" TEXT,
    "Class" INTEGER,
    "Base" TEXT,
    "Offers" JSON
);
