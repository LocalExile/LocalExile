-- Client table AncestralTrialMonsters (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AncestralTrialMonsters" (
    "_row" INTEGER PRIMARY KEY,
    "Unit" INTEGER,
    "Monster" INTEGER,
    "Animation" INTEGER,
    "__col3" INTEGER,
    "Achievement" JSON,
    "__col5" BOOLEAN,
    "__col6" BOOLEAN,
    "__col7" BOOLEAN,
    "__col8" BOOLEAN
);
