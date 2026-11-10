-- Client table PassiveSkillTreeTutorial (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PassiveSkillTreeTutorial" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "CharactersKey" INTEGER,
    "PassiveChoiceA" INTEGER,
    "ChoiceA_Description" TEXT,
    "ChoiceB_Description" TEXT,
    "PassiveChoiceB" INTEGER,
    "ChoiceA_PassiveTreeURL" TEXT,
    "ChoiceB_PassiveTreeURL" TEXT,
    "NotableChoiceA" INTEGER,
    "NotableChoiceB" INTEGER
);
