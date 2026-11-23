-- Client table TriggeredAudioEventVolumeOverrides (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "TriggeredAudioEventVolumeOverrides" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" REAL,
    "Actions" JSON,
    "__col3" REAL
);
