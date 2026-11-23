-- Client table PassiveTreeExpansionJewels (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PassiveTreeExpansionJewels" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "PassiveTreeExpansionJewelSizesKey" INTEGER,
    "MinNodes" INTEGER,
    "MaxNodes" INTEGER,
    "SmallIndices" JSON,
    "NotableIndices" JSON,
    "SocketIndices" JSON,
    "TotalIndices" INTEGER
);
