-- The nine ailment and avoidance tattoo effects (issue #372). Each row is the replacement passive's canonical stat
-- set from the client's PassiveSkillOverrides (Stats + StatValues): the apply path replaces the allocated passive's
-- stats with these, so the replacement matches the client's own effect text.
-- Ailment chances and avoidance: the Panels stage publishes each display total with its base_* sources summed in
-- (StatSnapshotCache), so the existing ailment, avoidance, stun-recovery and bleed readers pick these stats up
-- without a code change.
INSERT INTO catalog_entries (kind, key, payload, enabled) VALUES
('tattoo_effect', 'Tattoo of the Arohongui Scout', '{"Name":"Tattoo of the Arohongui Scout","Stats":[{"Stat":"base_avoid_chill_%","Value":10},{"Stat":"base_avoid_freeze_%","Value":10}]}', 1),
('tattoo_effect', 'Tattoo of the Arohongui Warmonger', '{"Name":"Tattoo of the Arohongui Warmonger","Stats":[{"Stat":"base_chance_to_freeze_%","Value":5}]}', 1),
('tattoo_effect', 'Tattoo of the Kitava Rebel', '{"Name":"Tattoo of the Kitava Rebel","Stats":[{"Stat":"base_avoid_bleed_%","Value":10}]}', 1),
('tattoo_effect', 'Tattoo of the Kitava Shaman', '{"Name":"Tattoo of the Kitava Shaman","Stats":[{"Stat":"bleed_on_hit_with_attacks_%","Value":5}]}', 1),
('tattoo_effect', 'Tattoo of the Ngamahu Warmonger', '{"Name":"Tattoo of the Ngamahu Warmonger","Stats":[{"Stat":"base_chance_to_ignite_%","Value":5}]}', 1),
('tattoo_effect', 'Tattoo of the Tasalio Tideshifter', '{"Name":"Tattoo of the Tasalio Tideshifter","Stats":[{"Stat":"base_avoid_stun_%","Value":10}]}', 1),
('tattoo_effect', 'Tattoo of the Tawhoa Scout', '{"Name":"Tattoo of the Tawhoa Scout","Stats":[{"Stat":"base_avoid_poison_%","Value":10}]}', 1),
('tattoo_effect', 'Tattoo of the Tawhoa Shaman', '{"Name":"Tattoo of the Tawhoa Shaman","Stats":[{"Stat":"base_chance_to_poison_on_hit_%","Value":5}]}', 1),
('tattoo_effect', 'Tattoo of the Valako Shaman', '{"Name":"Tattoo of the Valako Shaman","Stats":[{"Stat":"base_chance_to_shock_%","Value":5}]}', 1);
