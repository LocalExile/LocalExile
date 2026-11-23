-- Client table Acts (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Acts" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Part" INTEGER,
    "__col2" TEXT,
    "__col3" INTEGER,
    "ActNumber" INTEGER,
    "WorldPanelImage" TEXT,
    "WorldPanelImageEpilogue" TEXT,
    "__col7" INTEGER,
    "IsPostGame" BOOLEAN,
    "__col9" INTEGER,
    "__col10" JSON
);
