-- Client table MapInhabitants (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapInhabitants" (
    "_row" INTEGER PRIMARY KEY,
    "StatsKey" INTEGER,
    "MonsterPacksKeys" JSON
);
