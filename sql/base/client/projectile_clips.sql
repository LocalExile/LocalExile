-- Generated table projectile_clips (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "projectile_clips" (
    "_row" INTEGER PRIMARY KEY,
    "type" TEXT,
    "amd" TEXT,
    "ao" TEXT,
    "flight_index" INTEGER,
    "flight_ms" INTEGER,
    "flight_name" TEXT,
    "flight_once" BOOLEAN,
    "impact_index" INTEGER,
    "impact_ms" INTEGER,
    "impact_name" TEXT,
    "impact_once" BOOLEAN,
    "default" TEXT
);
