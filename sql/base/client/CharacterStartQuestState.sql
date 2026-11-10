-- Client table CharacterStartQuestState (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CharacterStartQuestState" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "QuestKeys" JSON,
    "QuestFlags" JSON,
    "NPCs" JSON,
    "MapPinsKeys" JSON,
    "__col5" JSON,
    "Areas" JSON
);
