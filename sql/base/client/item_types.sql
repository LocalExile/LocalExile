-- Generated table item_types (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "item_types" (
    "_row" INTEGER PRIMARY KEY,
    "type" TEXT,
    "chain" JSON,
    "missing" JSON,
    "abstract" BOOLEAN,
    "components" JSON,
    "read" INTEGER,
    "tags" JSON,
    "quest_grant_flag" TEXT,
    "quest_remove_flag" TEXT,
    "quest_use_flag" TEXT,
    "quest_extra_flag" TEXT
);
