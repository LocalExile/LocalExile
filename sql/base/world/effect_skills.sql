-- How a skill an effect rule releases is carried out, per GrantedEffects Id (Modules/Core/Game/Effects/
-- GrantedSkillRules.cs): the executor ("area": hits where it goes off) and what the player sees. Only rows with
-- enabled = 1 load. checked = 1 once the art and the size were compared with the real game; 0 is a best guess.
CREATE TABLE effect_skills (
    effect TEXT NOT NULL PRIMARY KEY,
    how TEXT NOT NULL,
    art TEXT,
    scale REAL NOT NULL DEFAULT 1,
    seconds REAL NOT NULL DEFAULT 2,
    radius INTEGER,
    hits INTEGER NOT NULL DEFAULT 1 CHECK (hits >= 1),
    every REAL NOT NULL DEFAULT 0 CHECK (every >= 0),
    spread INTEGER NOT NULL DEFAULT 0 CHECK (spread >= 0),
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    checked INTEGER NOT NULL DEFAULT 0 CHECK (checked IN (0, 1)),
    note TEXT
) STRICT;
