-- Client table Animation (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Animation" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" BOOLEAN,
    "__col2" BOOLEAN,
    "__col3" BOOLEAN,
    "Mainhand_AnimationKey" INTEGER,
    "Offhand_AnimationKey" INTEGER,
    "__col6" BOOLEAN,
    "Stat" INTEGER,
    "__col8" INTEGER,
    "__col9" BOOLEAN,
    "__col10" INTEGER,
    "__col11" JSON
);
