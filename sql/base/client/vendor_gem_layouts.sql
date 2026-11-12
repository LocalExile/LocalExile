-- Generated table vendor_gem_layouts (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "vendor_gem_layouts" (
    "_row" INTEGER PRIMARY KEY,
    "vendor" TEXT,
    "tab" INTEGER,
    "row" INTEGER,
    "gems" JSON
);
