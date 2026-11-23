-- Client table ActiveSkillRequirements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ActiveSkillRequirements" (
    "_row" INTEGER PRIMARY KEY,
    "GrantedEffect" INTEGER,
    "BuffDefinition" INTEGER,
    "__col2" INTEGER,
    "__col3" BOOLEAN,
    "__col4" INTEGER,
    "Stat" INTEGER,
    "Icon" INTEGER
);
