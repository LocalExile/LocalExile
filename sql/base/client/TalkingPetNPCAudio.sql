-- Client table TalkingPetNPCAudio (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "TalkingPetNPCAudio" (
    "_row" INTEGER PRIMARY KEY,
    "Id" INTEGER,
    "NPC" INTEGER,
    "NPCAudio" JSON,
    "Animation" TEXT,
    "__col4" BOOLEAN,
    "NPCAudio2" JSON
);
