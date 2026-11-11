-- Match the increased automatic pickup radius without replacing operator overrides.
UPDATE system_settings SET value = 20.0 WHERE key = 'gold_auto_loot_radius' AND value = 15.0;
