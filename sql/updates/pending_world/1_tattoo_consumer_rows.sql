-- The three current tattoo effects whose consumers are new (issue #372): Herald buff effect, guard-skill duration
-- and Fortify on a melee stun. Each row is the replacement passive's canonical stat set from the client's
-- PassiveSkillOverrides (Stats + StatValues).
-- Consumers: herald_effect_on_self_+% scales the herald buff stats (AuraCatalog.RawStats from Skills);
-- guard_skill_effect_duration_+% adds into the skill_effect_duration_+% sum of a triggered guard
-- (TriggeredGuards.GuardDuration); chance_to_fortify_on_melee_stun_% is rolled by the successful melee stun in
-- HitReaction.TryFlinch and grants FortificationGainModel's damage-based stacks.

INSERT INTO tattoo_effects (name, enabled) VALUES
('Tattoo of the Arohongui Shaman', 1),
('Tattoo of the Rongokurai Guard', 1),
('Tattoo of the Tukohama Warrior', 1);

INSERT INTO tattoo_effect_stats (tattoo, ord, stat, value) VALUES
('Tattoo of the Arohongui Shaman', 0, 'herald_effect_on_self_+%', 5),
('Tattoo of the Rongokurai Guard', 0, 'guard_skill_effect_duration_+%', 6),
('Tattoo of the Tukohama Warrior', 0, 'chance_to_fortify_on_melee_stun_%', 5);
