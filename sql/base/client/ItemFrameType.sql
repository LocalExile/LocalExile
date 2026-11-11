-- Client table ItemFrameType (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ItemFrameType" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" BOOLEAN,
    "DoubleLine" BOOLEAN,
    "HeaderSingle" TEXT,
    "HeaderDouble" TEXT,
    "HardmodeHeaderSingle" TEXT,
    "HardmodeHeaderDouble" TEXT,
    "Color" JSON,
    "Separator" TEXT,
    "__col9" BOOLEAN,
    "Rarity" INTEGER,
    "DisplayString" INTEGER,
    "ColorMarkup" TEXT,
    "__col13" INTEGER
);
