-- The nine plain-stat tattoo effects whose gameplay consumers already exist (issue #372: the skill, minion and flask
-- rows after the ailment/avoidance group). Each row is the replacement passive's canonical stat set from the
-- client's PassiveSkillOverrides (Stats + StatValues).
-- Consumers: minion_maximum_life_+% (GolemSkills, PlayerMinions), totem_life_+% (Totems),
-- base_life_leech_from_attack_damage_permyriad (LeechSources), maim_on_hit_% and attacks_chance_to_blind_on_hit_%
-- (HitDebuffs), mark_skills_curse_effect_+% (Curses), flask_duration_+% and flask_life_to_recover_+% (flask recovery)
-- and warcry_cooldown_speed_+% (SingleUseCooldown).
INSERT INTO catalog_entries (kind, key, payload, enabled) VALUES
('tattoo_effect', 'Tattoo of the Hinekora Warmonger', '{"Name":"Tattoo of the Hinekora Warmonger","Stats":[{"Stat":"minion_maximum_life_+%","Value":5}]}', 1),
('tattoo_effect', 'Tattoo of the Kitava Blood Drinker', '{"Name":"Tattoo of the Kitava Blood Drinker","Stats":[{"Stat":"base_life_leech_from_attack_damage_permyriad","Value":50}]}', 1),
('tattoo_effect', 'Tattoo of the Ngamahu Woodcarver', '{"Name":"Tattoo of the Ngamahu Woodcarver","Stats":[{"Stat":"totem_life_+%","Value":5}]}', 1),
('tattoo_effect', 'Tattoo of the Tasalio Bladedancer', '{"Name":"Tattoo of the Tasalio Bladedancer","Stats":[{"Stat":"maim_on_hit_%","Value":5}]}', 1),
('tattoo_effect', 'Tattoo of the Tasalio Scout', '{"Name":"Tattoo of the Tasalio Scout","Stats":[{"Stat":"mark_skills_curse_effect_+%","Value":4}]}', 1),
('tattoo_effect', 'Tattoo of the Tasalio Warrior', '{"Name":"Tattoo of the Tasalio Warrior","Stats":[{"Stat":"attacks_chance_to_blind_on_hit_%","Value":5}]}', 1),
('tattoo_effect', 'Tattoo of the Tawhoa Herbalist', '{"Name":"Tattoo of the Tawhoa Herbalist","Stats":[{"Stat":"flask_duration_+%","Value":4}]}', 1),
('tattoo_effect', 'Tattoo of the Tawhoa Naturalist', '{"Name":"Tattoo of the Tawhoa Naturalist","Stats":[{"Stat":"flask_life_to_recover_+%","Value":8}]}', 1),
('tattoo_effect', 'Tattoo of the Tukohama Warcaller', '{"Name":"Tattoo of the Tukohama Warcaller","Stats":[{"Stat":"warcry_cooldown_speed_+%","Value":8}]}', 1);
