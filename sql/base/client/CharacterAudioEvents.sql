-- Client table CharacterAudioEvents (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CharacterAudioEvents" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Event" INTEGER,
    "__col2" INTEGER,
    "__col3" INTEGER,
    "Goddess_CharacterTextAudioKeys" JSON,
    "JackTheAxe_CharacterTextAudioKeys" JSON,
    "__col6" BOOLEAN,
    "__col7" BOOLEAN,
    "__col8" JSON
);
