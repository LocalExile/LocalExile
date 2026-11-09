-- Client table BetrayalChoices (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BetrayalChoices" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Text" TEXT,
    "__col2" INTEGER,
    "Achievements" JSON
);
