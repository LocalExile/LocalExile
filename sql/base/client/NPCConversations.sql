-- Client table NPCConversations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NPCConversations" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "DialogueEvent" INTEGER,
    "NPCTextAudioKeys" JSON,
    "__col3" JSON,
    "__col4" INTEGER
);
