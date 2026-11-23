-- Client table AncestralTrialTribeOpinions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AncestralTrialTribeOpinions" (
    "_row" INTEGER PRIMARY KEY,
    "SourceTribe" INTEGER,
    "TargetTribe" INTEGER,
    "Opinion" INTEGER
);
