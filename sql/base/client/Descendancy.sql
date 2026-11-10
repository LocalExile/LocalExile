-- Client table Descendancy (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Descendancy" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "BackgroundImage" TEXT,
    "FlavourText" TEXT,
    "CoordinateRect" TEXT,
    "RGBFlavourTextColour" TEXT,
    "UnlockCondition" TEXT,
    "SoundEffectOpen" INTEGER,
    "SoundEffectAccept" INTEGER,
    "__col9" INTEGER,
    "PassiveTreeUIArt" INTEGER,
    "UIArt" INTEGER
);
