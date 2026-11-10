-- Client table CharacterStartStates (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CharacterStartStates" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "CharactersKey" INTEGER,
    "Level" INTEGER,
    "PassiveSkillsKeys" JSON,
    "CharacterStartStateSetKey" INTEGER,
    "HubArea" INTEGER,
    "CharacterStartQuestStateKeys" JSON,
    "__col8" BOOLEAN,
    "InfoText" TEXT,
    "Ascendancy" INTEGER
);
