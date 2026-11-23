-- Client table UniqueStashLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "UniqueStashLayout" (
    "_row" INTEGER PRIMARY KEY,
    "WordsKey" INTEGER,
    "ItemVisualIdentityKey" INTEGER,
    "UniqueStashTypesKey" INTEGER,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "OverrideWidth" INTEGER,
    "OverrideHeight" INTEGER,
    "ShowIfEmptyChallengeLeague" BOOLEAN,
    "ShowIfEmptyStandard" BOOLEAN,
    "RenamedVersion" INTEGER,
    "BaseVersion" INTEGER,
    "IsAlternateArt" BOOLEAN
);
