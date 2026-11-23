-- Client table SkillMorphDisplay (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SkillMorphDisplay" (
    "_row" INTEGER PRIMARY KEY,
    "Skill" INTEGER,
    "BuffDefinitions" JSON,
    "DDSFiles" JSON,
    "__col3" INTEGER,
    "Overlay" JSON,
    "__col5" INTEGER,
    "__col6" JSON,
    "__col7" BOOLEAN,
    "__col8" BOOLEAN
);
