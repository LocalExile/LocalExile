-- Client table BetrayalDialogue (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BetrayalDialogue" (
    "_row" INTEGER PRIMARY KEY,
    "Id" INTEGER,
    "__col1" INTEGER,
    "__col2" INTEGER,
    "__col3" JSON,
    "BetrayalTargetsKey" INTEGER,
    "__col5" INTEGER,
    "Job" INTEGER,
    "Relationship" JSON,
    "BetrayalUpgradesKey" INTEGER,
    "__col9" BOOLEAN,
    "__col10" JSON,
    "PossibleChoices" JSON,
    "__col12" BOOLEAN,
    "__col13" JSON,
    "NPCTextAudioKey" INTEGER,
    "__col15" JSON
);
