-- Client table TimeTrackingMapDeviceInterfaceArtVariation (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "TimeTrackingMapDeviceInterfaceArtVariation" (
    "_row" INTEGER PRIMARY KEY,
    "MapDevice" INTEGER,
    "Art" TEXT,
    "__col2" JSON,
    "__col3" JSON,
    "__col4" JSON
);
