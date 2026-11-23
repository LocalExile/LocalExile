-- Client table ItemisedCorpse (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ItemisedCorpse" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItem" INTEGER,
    "MonsterVariety" INTEGER,
    "MonsterAbilities" TEXT,
    "MonsterCategory" INTEGER
);
