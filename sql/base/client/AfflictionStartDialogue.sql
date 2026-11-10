-- Client table AfflictionStartDialogue (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AfflictionStartDialogue" (
    "_row" INTEGER PRIMARY KEY,
    "WorldAreasKey" INTEGER,
    "NPCTextAudioKey" INTEGER,
    "MonsterVarieties" JSON
);
