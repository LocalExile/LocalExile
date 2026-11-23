-- Client table UltimatumMapModifiers (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "UltimatumMapModifiers" (
    "_row" INTEGER PRIMARY KEY,
    "Stat" INTEGER,
    "Mods" JSON
);
