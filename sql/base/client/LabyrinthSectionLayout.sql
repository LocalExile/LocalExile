-- Client table LabyrinthSectionLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "LabyrinthSectionLayout" (
    "_row" INTEGER PRIMARY KEY,
    "LabyrinthSectionKey" INTEGER,
    "__col1" INTEGER,
    "LabyrinthSectionLayoutKeys" JSON,
    "LabyrinthSecretsKey0" INTEGER,
    "LabyrinthSecretsKey1" INTEGER,
    "LabyrinthAreasKey" INTEGER,
    "Float0" REAL,
    "Float1" REAL,
    "LabyrinthNodeOverridesKeys" JSON
);
