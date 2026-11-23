-- Client table NPCTextAudio (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NPCTextAudio" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Characters" JSON,
    "Text" TEXT,
    "Mono_AudioFile" TEXT,
    "Stereo_AudioFile" TEXT,
    "HasStereo" BOOLEAN,
    "__col6" BOOLEAN,
    "Video" TEXT,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "__col10" INTEGER,
    "NPCs" JSON,
    "NPCPortrait" INTEGER,
    "__col13" BOOLEAN,
    "__col14" REAL,
    "Cooldown" JSON,
    "__col16" INTEGER
);
