-- Client table GamepadButton (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "GamepadButton" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "XInput" TEXT,
    "Generic" TEXT,
    "DualShock" TEXT,
    "SteamInput" TEXT,
    "DualSense" TEXT
);
