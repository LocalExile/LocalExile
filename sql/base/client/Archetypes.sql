-- Client table Archetypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Archetypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "CharactersKey" INTEGER,
    "PassiveSkillTreeURL" TEXT,
    "AscendancyClassName" TEXT,
    "Description" TEXT,
    "UIImageFile" TEXT,
    "TutorialVideo_BKFile" TEXT,
    "__col7" INTEGER,
    "__col8" INTEGER,
    "__col9" REAL,
    "BackgroundImageFile" TEXT,
    "IsTemporary" BOOLEAN,
    "__col12" BOOLEAN,
    "ArchetypeImage" TEXT,
    "__col14" BOOLEAN,
    "__col15" BOOLEAN
);
