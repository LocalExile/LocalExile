-- Client table WindowCursors (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "WindowCursors" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "CursorDefault" TEXT,
    "CursorClick" TEXT,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "CursorHover" TEXT,
    "Description" TEXT,
    "IsEnabled" BOOLEAN,
    "__col8" INTEGER
);
