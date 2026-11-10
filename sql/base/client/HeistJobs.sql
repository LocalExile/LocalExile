-- Client table HeistJobs (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistJobs" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "RequiredSkillIcon" TEXT,
    "SkillIcon" TEXT,
    "__col4" REAL,
    "__col5" INTEGER,
    "MapIcon" TEXT,
    "Level_StatsKey" INTEGER,
    "Alert_StatsKey" INTEGER,
    "Alarm_StatsKey" INTEGER,
    "Cost_StatsKey" INTEGER,
    "ExperienceGain_StatsKey" INTEGER,
    "ConsoleBlueprintLegend" TEXT,
    "Description" TEXT
);
