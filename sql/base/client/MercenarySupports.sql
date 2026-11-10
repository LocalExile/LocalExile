-- Client table MercenarySupports (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MercenarySupports" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" INTEGER,
    "__col2" INTEGER,
    "__col3" INTEGER,
    "Stats" JSON,
    "StatValues" JSON,
    "Name" TEXT,
    "SupportFamily" INTEGER,
    "HASH16" INTEGER,
    "GemIcon" TEXT,
    "Tier" INTEGER,
    "__col11" JSON,
    "__col12" JSON
);
