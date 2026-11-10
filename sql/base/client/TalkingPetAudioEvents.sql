-- Client table TalkingPetAudioEvents (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "TalkingPetAudioEvents" (
    "_row" INTEGER PRIMARY KEY,
    "Event" TEXT,
    "__col1" INTEGER,
    "__col2" INTEGER,
    "__col3" INTEGER,
    "BaseItemTypeKey" JSON,
    "ItemClass" JSON,
    "RepeatCooldown" INTEGER
);
