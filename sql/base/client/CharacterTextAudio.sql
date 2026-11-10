-- Client table CharacterTextAudio (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CharacterTextAudio" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Text" TEXT,
    "SoundFile" TEXT,
    "ParrotSoundFile" TEXT,
    "__col4" INTEGER,
    "__col5" INTEGER
);
