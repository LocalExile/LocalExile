-- Client table NecropolisPackModTiers (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NecropolisPackModTiers" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Icon" TEXT,
    "Name" TEXT,
    "TextColours" JSON,
    "SpecialTier" BOOLEAN
);
