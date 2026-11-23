-- The buff a skill gives its user when an effect rule releases it, per GrantedEffects Id (Modules/Core/Game/Effects/
-- GrantedSkillRules.cs, executor "buff"). Either the skill's own routine (routine: a name registered in the game
-- module, "BloodRage") or a timed buff: the BuffDefinitions Id (buff) with a value for each of its stats (stats: JSON,
-- stat name to a number, or to the name of one of the skill's own stats to read it from). seconds: how long it lasts;
-- NULL takes the skill's base_skill_effect_duration with the character's duration modifiers. Only rows with
-- enabled = 1 load. checked = 1 once the buff and its values were compared with the real game; 0 is a best guess.
CREATE TABLE effect_buffs (
    effect TEXT NOT NULL PRIMARY KEY,
    buff TEXT,
    routine TEXT,
    seconds REAL CHECK (seconds IS NULL OR seconds > 0),
    stats TEXT NOT NULL DEFAULT '{}',
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    checked INTEGER NOT NULL DEFAULT 0 CHECK (checked IN (0, 1)),
    note TEXT,
    CHECK (buff IS NOT NULL OR routine IS NOT NULL)
) STRICT;
