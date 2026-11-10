-- Client table UITalkText (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "UITalkText" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "UITalkCategoriesKey" INTEGER,
    "OGGFile" TEXT,
    "Text" TEXT,
    "__col4" BOOLEAN,
    "NPCTextAudio" JSON
);
