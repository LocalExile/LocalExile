-- Client table AtlasNodeDefinition (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AtlasNodeDefinition" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Area1" INTEGER,
    "__col2" INTEGER,
    "__col3" INTEGER,
    "NotOnAtlas" BOOLEAN,
    "IsUniqueMap" BOOLEAN,
    "Node_DDSFile" TEXT,
    "IsNormalMap" BOOLEAN,
    "RequiresSpecificItem" BOOLEAN,
    "VoidstoneSlot" INTEGER,
    "MapDeviceLayout" INTEGER,
    "QuestStates" JSON,
    "__col12" BOOLEAN,
    "NodeDisplayName" INTEGER,
    "Area2" INTEGER,
    "__col15" BOOLEAN,
    "Header" INTEGER,
    "__col17" INTEGER
);
