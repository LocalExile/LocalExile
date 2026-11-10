-- Client table HeistNPCDialogue (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistNPCDialogue" (
    "_row" INTEGER PRIMARY KEY,
    "DialogueEventKey" INTEGER,
    "HeistNPCsKey" INTEGER,
    "AudioNormal" JSON,
    "AudioLoud" JSON,
    "__col4" INTEGER
);
