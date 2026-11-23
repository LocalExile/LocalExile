-- Client table Notifications (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Notifications" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" BOOLEAN,
    "__col2" BOOLEAN,
    "Message" TEXT,
    "__col4" TEXT,
    "__col5" INTEGER,
    "__col6" BOOLEAN,
    "__col7" BOOLEAN
);
