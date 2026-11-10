-- Client table AncestralTrialPositionType (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AncestralTrialPositionType" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "PlayerTotemIcon" TEXT,
    "Name" TEXT,
    "EnemyTotemIcon" TEXT,
    "PlayerTotemSmallIcon" TEXT,
    "EnemyTotemSmallIcon" TEXT
);
