-- Client table CompletionNotification (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CompletionNotification" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Art" TEXT,
    "Sound" INTEGER,
    "PopupMessage" INTEGER
);
