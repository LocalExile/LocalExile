-- The first parts of the effect rules: the triggers, conditions, timers, values, targets and actions rules are built
-- from, and the first quantities. built = 1: the game module has the part today (the first rules use it); the others are
-- named for the rules to come. More rows are added as stats are taken apart.
INSERT INTO fx_triggers (id, takes, built, origin, note) VALUES
('active', '', 1, 'always-on rules (Death Aura''s Death''s Oath)', 'For as long as the rule''s source lasts: starts when the source appears on the actor (an item worn, a flask in effect, a buff, an area modifier) and stops when it goes. No chance, cooldown or use limit.'),
('hit', 'who: self, totem, trap, mine (default self)', 1, 'chance to gain Onslaught on hit', 'The actor''s hit landed on an enemy. Gives the enemy hit, whether it was critical, a spell or melee, and the skill.'),
('kill', '', 1, 'chance to gain Onslaught on kill', 'The actor killed an enemy. Gives the enemy killed and its rarity.'),
('when_hit', '', 1, 'gain a charge when hit', 'An enemy''s hit landed on the actor. Gives the attacker, and whether it was blocked, a spell or critical.'),
('block', '', 1, 'Shield Shatter on block (The Surrender family)', 'The actor blocked a hit. Gives the attacker and whether it was a spell.'),
('stun', '', 1, 'gain a charge on stun', 'The actor stunned an enemy.'),
('skill_use', 'skill types to listen for (attack, spell, melee), or any', 1, 'trigger a skill when you attack (Storm Cascade)', 'The actor used a skill itself (not a triggered one). Gives the skill and where it was aimed.'),
('flask_use', 'which flask: this one (the source), or any', 0, 'flask suffixes "during Effect"', 'The actor used a flask. With the source''s own flask, the rule runs for that flask''s effect.'),
('area_enter', '', 0, 'map and league rules', 'The actor entered an area. Gives the area.'),
('area_made', '', 0, 'map mods that place things (burning ground patches)', 'An area was generated. The actor is the area.'),
('item_use', '', 0, 'currency (Chaos Orb)', 'An item was used on another item. The actor is the item used; gives the target item.'),
('interval', 'seconds', 0, '"every 4 seconds" mods', 'Fires again and again while the source lasts, the given seconds apart.'),
('critical', '', 0, '"on Critical Strike" mods', 'The actor''s hit was a critical strike. As hit, for rules that only listen for criticals.'),
('death', 'whose: self, minion, enemy nearby', 0, '"when you die", "when a minion dies" mods', 'The named one died.'),
('custom', 'name', 0, 'rule chains', 'Another rule raised this named trigger with the action "raise".');
INSERT INTO fx_conditions (id, takes, built, origin, note) VALUES
('chance', 'percent: a value', 1, 'chance to gain Onslaught on kill', 'Passes that often, rolled each time the rule fires.'),
('cooldown', 'seconds: a value', 1, 'triggered spells (0.25 s on Storm Cascade)', 'Passes only if the rule has not fired for that long.'),
('uses', 'count: a value, and what refills it', 1, 'triggered marks', 'Passes while the rule has uses left.'),
('enemy_rarity', 'rarities: normal, magic, rare, unique', 1, 'triggered marks ("when you hit a Rare or Unique Enemy")', 'The enemy of the trigger has one of the rarities.'),
('has_mark', '', 1, 'triggered marks ("and have no Mark")', 'The actor has a Mark on an enemy. Use with negate for "have no Mark".'),
('skill_type', 'types: attack, spell, melee, projectile...', 1, '"when you use a melee skill"', 'The trigger''s skill has one of the types.'),
('low_life', '', 0, '"while on Low Life" mods', 'The actor''s life is at or below its low life threshold.'),
('full_life', '', 0, '"while on Full Life" mods', 'The actor''s life is full.'),
('recently', 'event: an fx_triggers id, seconds (default 4)', 0, '"if you''ve Killed Recently" mods', 'The event happened to the actor within the last seconds.'),
('weapon', 'classes: axe, sword, bow, shield, dual wielding, unarmed...', 0, '"with Axes", "while holding a Shield" mods', 'The actor wields one of them.'),
('has_buff', 'buff: a BuffDefinitions Id, or a charge type', 0, '"while you have Onslaught" mods', 'The actor has the buff or at least one of the charges.'),
('stat_at_least', 'stat, amount', 0, '"if you have at least 200 Strength" mods', 'The actor''s stat is at least the amount.'),
('item_is', 'rarity, class, corrupted, mirrored, influenced', 0, 'currency (a Chaos Orb needs a rare, uncorrupted item)', 'The target item is as described.'),
('area_is', 'kind: map, town, hideout, league area', 0, 'map rules', 'The area is of the kind.'),
('during_flask', 'which: this one, any, life, mana, utility', 0, '"during any Flask Effect" mods', 'A flask''s effect is running on the actor.');
INSERT INTO fx_timers (id, takes, built, origin, note) VALUES
('duration', 'seconds: a value', 1, 'Onslaught for 4 seconds', 'How long the action''s result lasts.'),
('cooldown', 'seconds: a value', 1, 'triggered spells', 'How long before the rule can fire again (the condition "cooldown" names it).'),
('source', '', 0, 'flask suffixes "during Effect"', 'For as long as the rule''s source lasts, and no longer: a flask''s effect, a buff, an item worn.'),
('delay', 'seconds: a value', 0, 'delayed blasts', 'How long after the trigger the action happens.'),
('repeat', 'seconds: a value, count', 0, 'pulses (Abberath''s Fury)', 'The action happens again that often, the count times or while the source lasts.'),
('window', 'seconds: a value', 0, '"Recently" (4 seconds)', 'How far back the condition "recently" looks.');
INSERT INTO fx_values (id, takes, built, origin, note) VALUES
('roll', '', 1, 'every mod', 'The value the rule''s source stat has on the actor: the mod''s roll, summed over everything that gives the stat.'),
('fixed', 'number', 1, 'Onslaught for 4 seconds', 'A number written in the rule.'),
('stat', 'stat, scale, fallback', 1, 'onslaught_time_granted_on_kill_ms', 'Another stat of the actor, times the scale; the fallback when the actor does not have it.'),
('skill_stat', 'stat, scale', 1, 'a triggered skill''s own duration', 'A stat of the skill the rule releases, at the level the item grants.'),
('area_level', 'scale', 0, 'map and monster rules', 'The level of the area the actor is in.'),
('counter', 'name', 0, 'stacking mods', 'A number stored on the actor under the name by the action "count".'),
('per', 'of: a value, each: number, cap', 0, '"per 10 Strength", "per Endurance Charge"', 'Another value divided into steps: the amount for each step, up to the cap.'),
('event', 'which: damage dealt, damage taken, life lost, mana spent', 0, '"% of Damage dealt" mods', 'A number the trigger carries.');
INSERT INTO fx_targets (id, takes, built, origin, note) VALUES
('self', '', 1, 'every mod', 'The actor the rule belongs to.'),
('enemy', '', 1, 'curse on hit', 'The enemy of the trigger: the one hit, killed, or that hit the actor.'),
('enemies_near', 'radius, of: self or the trigger''s enemy or a point, count', 1, 'area skills on kill, Death Aura', 'Enemies within the radius.'),
('allies_near', 'radius, players only or with minions', 0, 'auras', 'Allies within the radius, the actor included unless said.'),
('players_area', '', 0, 'map mods on players', 'Every player in the area.'),
('enemies_area', '', 0, 'map mods on monsters', 'Every enemy of the actor in the area. For an area as actor: its monsters.'),
('minions', 'kinds', 0, 'minion mods', 'The actor''s minions.'),
('item_self', '', 0, '"Has no Sockets", local mods', 'The item the rule comes from.'),
('item_target', '', 0, 'currency', 'The item a used item was used on.'),
('area', '', 0, 'map mods', 'The area itself: its drops, its monster packs, its ground.'),
('ground', 'at: self, the trigger''s enemy, random points; count', 0, 'burning ground, caustic ground', 'A point, or points, on the ground.');
INSERT INTO fx_actions (id, takes, built, origin, note) VALUES
('quantity', 'quantity: an fx_quantities id; how: flat, increased, more...', 1, 'every plain number mod; the "stats" kind of the first rules', 'Changes one of the target''s numbers by the value for the timer''s length. The long form of an fx_modifiers row.'),
('buff', 'buff: a BuffDefinitions Id or an effect_buffs row; stats', 1, 'Onslaught on kill, Blood Rage', 'Gives the target a buff for the timer''s length.'),
('charge', 'type: endurance, frenzy, power...; count', 1, 'gain a Frenzy Charge on kill', 'Gives the target charges.'),
('skill', 'skill: a GrantedEffects Id, or "granted" for the one the source item grants; level', 1, 'triggered marks and spells', 'Releases a skill at the target, carried out as effect_skills says.'),
('damage', 'type, amount', 0, 'reflect, thorns', 'Deals damage to the target as a hit or over time.'),
('recover', 'pool: life, mana, energy shield; amount', 0, '"gain 20 Life on kill"', 'Gives the target life, mana or energy shield back, at once or over the timer.'),
('ground', 'kind: an fx_grounds id; radius', 0, 'burning ground', 'Places a ground patch at the target for the timer''s length.'),
('projectile', 'which: an fx_projectiles id; count, pattern', 0, '"fires an additional projectile"', 'Fires projectiles from the actor at the target.'),
('summon', 'which, count, limit', 0, 'spiders on kill (Arakaali''s Fang)', 'Summons minions for the actor at the target.'),
('item', 'what: set sockets, reroll mods, add a mod, remove a mod, set quality, corrupt, use up', 0, 'currency; "Has no Sockets"', 'Changes the target item.'),
('area', 'what: add packs, add patches, change drops', 0, 'map mods', 'Changes the target area.'),
('drop', 'what, count', 0, '"drops an additional Chaos Orb"', 'Makes the target drop items.'),
('count', 'name, change: add, set, reset', 0, 'stacking mods', 'Changes a stored counter on the target, for the value "counter" to read.'),
('raise', 'name', 0, 'rule chains', 'Raises a custom trigger on the target for other rules to listen for.'),
('remove', 'what: a buff, charges, an ailment, a curse', 0, '"removes Bleeding" flasks', 'Takes something off the target.');
INSERT INTO fx_quantities (id, unit, formula, built, origin, note) VALUES
('maximum_life', 'life', 'code', 1, '+# to maximum Life', 'The actor''s maximum life.'),
('action_speed', '%', 'code', 1, 'Action Speed cannot be modified to below base value', 'How fast the actor acts: a multiplier on its attack, cast and movement speed.'),
('movement_speed', '%', 'code', 1, '#% increased Movement Speed', 'How fast the actor moves.'),
('knockback_taken', '', 'code', 1, 'Cannot be Knocked Back', 'Whether and how far the actor is knocked back.'),
('item_quantity', '%', 'code', 1, '#% increased Quantity of Items found in this Area', 'How many more items drop.');
