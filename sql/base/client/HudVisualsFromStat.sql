-- Client table HudVisualsFromStat (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HudVisualsFromStat" (
    "_row" INTEGER PRIMARY KEY,
    "Stat" INTEGER,
    "LifeVisual" INTEGER,
    "EnergyShieldVisual" INTEGER,
    "ManaVisual" INTEGER,
    "TradeVisual" INTEGER,
    "ManaFlaskVisual" INTEGER,
    "__col6" BOOLEAN
);
