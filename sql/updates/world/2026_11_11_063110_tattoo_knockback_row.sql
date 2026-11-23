-- The last current tattoo effect (issue #372): the Tukohama Brawler's knockback on hit. The row is the replacement
-- passive's canonical stat set from the client's PassiveSkillOverrides (Stats + StatValues).
-- Consumer: Game.KnockbackOnHitLocked rolls it for every player hit that lands (called from AilmentsOnHitLocked),
-- pushes the monster 2 cells away from the player (scaled by knockback_distance_+%), interrupts its action and hands
-- the destination to the AI tick, which presents it with InstanceProtocol.Teleport.
INSERT INTO catalog_entries (kind, key, payload, enabled) VALUES
('tattoo_effect', 'Tattoo of the Tukohama Brawler', '{"Name":"Tattoo of the Tukohama Brawler","Stats":[{"Stat":"base_global_chance_to_knockback_%","Value":10}]}', 1);
