-- Numeric server settings by key (socket caps, rates, limits); read by SystemSettings.
CREATE TABLE system_settings (
    key TEXT PRIMARY KEY,
    value REAL NOT NULL
) STRICT;
