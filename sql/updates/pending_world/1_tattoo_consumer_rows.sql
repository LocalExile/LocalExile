-- The three current tattoo effects whose consumers are new (issue #372): Herald buff effect, guard-skill duration
-- and Fortify on a melee stun. Each row is the replacement passive's canonical stat set from the client's
-- PassiveSkillOverrides (Stats + StatValues).
-- Consumers: herald_effect_on_self_+% scales the herald buff stats (AuraCatalog.RawStats from Skills);
-- guard_skill_effect_duration_+% adds into the skill_effect_duration_+% sum of a triggered guard
-- (TriggeredGuards.GuardDuration); chance_to_fortify_on_melee_stun_% is rolled by the successful melee stun in
-- HitReaction.TryFlinch and grants FortificationGainModel's damage-based stacks.
INSERT INTO catalog_entries (kind, key, payload, enabled) VALUES
('tattoo_effect', 'Tattoo of the Arohongui Shaman', '{"Name":"Tattoo of the Arohongui Shaman","Stats":[{"Stat":"herald_effect_on_self_+%","Value":5}]}', 1),
('tattoo_effect', 'Tattoo of the Rongokurai Guard', '{"Name":"Tattoo of the Rongokurai Guard","Stats":[{"Stat":"guard_skill_effect_duration_+%","Value":6}]}', 1),
('tattoo_effect', 'Tattoo of the Tukohama Warrior', '{"Name":"Tattoo of the Tukohama Warrior","Stats":[{"Stat":"chance_to_fortify_on_melee_stun_%","Value":5}]}', 1);
