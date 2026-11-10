-- Client table QuestStates (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "QuestStates" (
    "_row" INTEGER PRIMARY KEY,
    "Quest" INTEGER,
    "Order" INTEGER,
    "FlagsPresent" JSON,
    "FlagsMissing" JSON,
    "Text" TEXT,
    "__col5" BOOLEAN,
    "Message" TEXT,
    "MapPinsKeys1" JSON,
    "__col8" INTEGER,
    "MapPinsTexts" JSON,
    "MapPinsKeys2" JSON,
    "Area" JSON,
    "__col12" BOOLEAN,
    "__col13" JSON,
    "__col14" JSON,
    "__col15" INTEGER,
    "SoundEffect" INTEGER,
    "__col17" TEXT
);
