-- Character saves, stashes and the character list as tables instead of one JSON document each. The Gateway moves the
-- saves over the first time it opens the database (SaveRows.Migrate): it writes each save into these tables, reads it
-- back, and stops without changing anything when the two differ. The JSON tables (characters, deleted_characters,
-- stashes, stash_tabs, account_characters_json) stay as they were, as a backup; nothing reads them after the move.
-- A list of plain values (passive ids, quest flags, an item's links) is a JSON array column; null and [] differ there.
-- What a module or a newer build saved without this build knowing its shape (module data, unknown fields) stays JSON.

-- One row per character save, and per deleted character's save (deleted_utc set): a new character of a deleted one's
-- name starts over, and the old save can be put back by clearing deleted_utc.
CREATE TABLE character_saves (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE,
    deleted_utc TEXT,
    has_inventories INTEGER NOT NULL CHECK (has_inventories IN (0, 1)),
    experience INTEGER,
    passives TEXT CHECK (passives IS NULL OR json_valid(passives)),
    waypoints TEXT CHECK (waypoints IS NULL OR json_valid(waypoints)),
    visited TEXT CHECK (visited IS NULL OR json_valid(visited)),
    quest_flags TEXT CHECK (quest_flags IS NULL OR json_valid(quest_flags)),
    extra_passive_points INTEGER,
    gold INTEGER,
    refund_points INTEGER,
    portal_cosmetic INTEGER,
    active_auras TEXT CHECK (active_auras IS NULL OR json_valid(active_auras)),
    petrified_blood INTEGER CHECK (petrified_blood IN (0, 1)),
    selected_hideout TEXT,
    life INTEGER,
    mana INTEGER,
    vitals_character TEXT,
    vitals_session TEXT,
    vitals_retired INTEGER CHECK (vitals_retired IN (0, 1)),
    cluster_passives TEXT CHECK (cluster_passives IS NULL OR json_valid(cluster_passives)),
    sulphite INTEGER,
    atlas_passive_presets TEXT CHECK (atlas_passive_presets IS NULL OR json_valid(atlas_passive_presets)),
    atlas_passive_points INTEGER,
    selected_atlas_layout TEXT,
    quest_refunds_credited TEXT CHECK (quest_refunds_credited IS NULL OR json_valid(quest_refunds_credited)),
    pantheon_major TEXT,
    pantheon_minor TEXT,
    attack_in_place_slots INTEGER,
    skill_bar_mask52 INTEGER,
    stance TEXT,
    unknown TEXT CHECK (unknown IS NULL OR json_valid(unknown))
) STRICT;
CREATE UNIQUE INDEX character_saves_name ON character_saves (name) WHERE deleted_utc IS NULL;

-- The inventories a save has (Inventories.dat rows), items or not.
CREATE TABLE character_inventories (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    inventory INTEGER NOT NULL,
    PRIMARY KEY (save, inventory)
) STRICT;

CREATE TABLE character_skill_bar (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    slot INTEGER NOT NULL,
    skill TEXT NOT NULL,
    PRIMARY KEY (save, slot)
) STRICT;

CREATE TABLE character_masteries (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    node INTEGER NOT NULL,
    effect INTEGER NOT NULL,
    PRIMARY KEY (save, node)
) STRICT;

-- The Cosmetics tab: worn records by MicrotransactionSlot (slot) or by skill (skill_cosmetics).
CREATE TABLE character_cosmetics (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    slot INTEGER NOT NULL,
    record INTEGER NOT NULL,
    option INTEGER NOT NULL,
    hash INTEGER,
    PRIMARY KEY (save, slot)
) STRICT;

CREATE TABLE character_skill_cosmetics (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    skill TEXT NOT NULL,
    record INTEGER NOT NULL,
    option INTEGER NOT NULL,
    hash INTEGER,
    PRIMARY KEY (save, skill)
) STRICT;

CREATE TABLE character_hideout_placements (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    area TEXT NOT NULL,
    key TEXT NOT NULL,
    x INTEGER NOT NULL,
    y INTEGER NOT NULL,
    rotation REAL NOT NULL,
    PRIMARY KEY (save, area, key)
) STRICT;

CREATE TABLE character_monster_statues (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    area TEXT NOT NULL,
    ord INTEGER NOT NULL,
    x INTEGER NOT NULL,
    y INTEGER NOT NULL,
    rotation REAL NOT NULL,
    monster INTEGER NOT NULL,
    scale INTEGER NOT NULL,
    variation INTEGER NOT NULL,
    PRIMARY KEY (save, area, ord)
) STRICT;

CREATE TABLE character_tattoos (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    node INTEGER NOT NULL,
    tattoo TEXT NOT NULL,
    PRIMARY KEY (save, node)
) STRICT;

CREATE TABLE character_tattoo_keystones (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    node INTEGER NOT NULL,
    keystone INTEGER NOT NULL,
    PRIMARY KEY (save, node)
) STRICT;

-- A module's saved text by key (IPlayerWorld.CharacterData); its shape is the module's.
CREATE TABLE character_module_data (
    save INTEGER NOT NULL REFERENCES character_saves (id),
    key TEXT NOT NULL,
    value TEXT NOT NULL,
    PRIMARY KEY (save, key)
) STRICT;

-- The opening tutorial's progress; its two gems are items (role tutorial_skill, tutorial_support).
CREATE TABLE character_tutorials (
    save INTEGER PRIMARY KEY REFERENCES character_saves (id),
    tutorial_id TEXT,
    talked INTEGER NOT NULL CHECK (talked IN (0, 1)),
    zombie_killed INTEGER NOT NULL CHECK (zombie_killed IN (0, 1)),
    chest_opened INTEGER NOT NULL CHECK (chest_opened IN (0, 1)),
    skill_claimed INTEGER NOT NULL CHECK (skill_claimed IN (0, 1)),
    support_claimed INTEGER NOT NULL CHECK (support_claimed IN (0, 1)),
    weapon_prompt_heard INTEGER NOT NULL CHECK (weapon_prompt_heard IN (0, 1)),
    wake_up_shown INTEGER NOT NULL CHECK (wake_up_shown IN (0, 1))
) STRICT;

-- The main ascendancy (family null: none chosen) with its own nodes and awards.
CREATE TABLE character_ascendancies (
    save INTEGER PRIMARY KEY REFERENCES character_saves (id),
    family TEXT,
    ascendancy TEXT,
    revision TEXT,
    selection_unknown TEXT CHECK (selection_unknown IS NULL OR json_valid(selection_unknown)),
    nodes TEXT CHECK (nodes IS NULL OR json_valid(nodes)),
    awards TEXT CHECK (awards IS NULL OR json_valid(awards)),
    unknown TEXT CHECK (unknown IS NULL OR json_valid(unknown))
) STRICT;

-- One account stash (or guild stash, account "guild:...") per account and league key.
CREATE TABLE stash_saves (
    id INTEGER PRIMARY KEY,
    account TEXT NOT NULL UNIQUE COLLATE NOCASE,
    last_tab INTEGER NOT NULL,
    next_item_id INTEGER NOT NULL,
    gold INTEGER NOT NULL,
    delve TEXT CHECK (delve IS NULL OR json_valid(delve)),
    league_quest_flags TEXT CHECK (league_quest_flags IS NULL OR json_valid(league_quest_flags)),
    unknown TEXT CHECK (unknown IS NULL OR json_valid(unknown))
) STRICT;

CREATE TABLE stash_tab_headers (
    stash INTEGER NOT NULL REFERENCES stash_saves (id),
    tab INTEGER NOT NULL,
    name TEXT NOT NULL,
    type INTEGER NOT NULL,
    r INTEGER NOT NULL,
    g INTEGER NOT NULL,
    b INTEGER NOT NULL,
    affinities INTEGER NOT NULL,
    position INTEGER,
    colour INTEGER NOT NULL,
    unique_category INTEGER NOT NULL,
    parent INTEGER NOT NULL,
    PRIMARY KEY (stash, tab)
) STRICT;

CREATE TABLE stash_module_data (
    stash INTEGER NOT NULL REFERENCES stash_saves (id),
    key TEXT NOT NULL,
    value TEXT NOT NULL,
    PRIMARY KEY (stash, key)
) STRICT;

-- Every saved item, of a character (character) or a stash (stash). A placed item is in an inventory (a character's
-- Inventories.dat row, a stash's tab) at placement id and x, y; the gems in its sockets and the item it was before a
-- change (original) are items whose parent is it, in the same inventory. ord: the place in the inventory's list, or the
-- socket. The columns after role are CharacterSaves.SavedItem's.
CREATE TABLE items (
    id INTEGER PRIMARY KEY,
    character INTEGER REFERENCES character_saves (id),
    stash INTEGER REFERENCES stash_saves (id),
    inventory INTEGER,
    role TEXT NOT NULL CHECK (role IN ('placed', 'socketed', 'original', 'start_weapon', 'tutorial_skill', 'tutorial_support')),
    parent INTEGER REFERENCES items (id),
    ord INTEGER NOT NULL,
    placement INTEGER,
    x INTEGER,
    y INTEGER,
    type INTEGER NOT NULL,
    item_level INTEGER NOT NULL,
    quality INTEGER NOT NULL,
    charges INTEGER NOT NULL,
    stack_size INTEGER NOT NULL,
    sockets TEXT CHECK (sockets IS NULL OR json_valid(sockets)),
    links TEXT CHECK (links IS NULL OR json_valid(links)),
    gem_experience INTEGER NOT NULL,
    rarity INTEGER NOT NULL,
    identified INTEGER NOT NULL CHECK (identified IN (0, 1)),
    has_mods INTEGER NOT NULL CHECK (has_mods IN (0, 1)),
    name TEXT CHECK (name IS NULL OR json_valid(name)),
    gem_variant INTEGER NOT NULL,
    corrupted INTEGER NOT NULL CHECK (corrupted IN (0, 1)),
    fractured INTEGER NOT NULL,
    mirrored INTEGER NOT NULL CHECK (mirrored IN (0, 1)),
    gem_flags INTEGER NOT NULL,
    visual INTEGER,
    defence_base_roll INTEGER NOT NULL,
    influences INTEGER NOT NULL,
    split INTEGER NOT NULL CHECK (split IN (0, 1)),
    synthesised INTEGER NOT NULL CHECK (synthesised IN (0, 1)),
    foreseeing INTEGER NOT NULL CHECK (foreseeing IN (0, 1)),
    foresee_seed INTEGER NOT NULL,
    alternate_quality_type INTEGER,
    CHECK ((character IS NULL) <> (stash IS NULL))
) STRICT;
CREATE INDEX items_character ON items (character, inventory);
CREATE INDEX items_stash ON items (stash, inventory);
CREATE INDEX items_parent ON items (parent);

-- An item's mods in order (has_mods = 0: a save from before mods, whose item gets its type's implicits again).
CREATE TABLE item_mods (
    item INTEGER NOT NULL REFERENCES items (id),
    ord INTEGER NOT NULL,
    mod TEXT NOT NULL,
    section INTEGER NOT NULL,
    stat_values TEXT NOT NULL CHECK (json_valid(stat_values)),
    PRIMARY KEY (item, ord)
) STRICT;

-- The character list Auth shows at login, as columns. The JSON rows stay in account_characters_json.
ALTER TABLE account_characters RENAME TO account_characters_json;
CREATE TABLE account_characters (
    id TEXT PRIMARY KEY,
    account_id TEXT NOT NULL,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    class INTEGER NOT NULL,
    level INTEGER NOT NULL,
    league TEXT NOT NULL,
    area_id TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    last_played_utc TEXT NOT NULL,
    last_town TEXT,
    portrait INTEGER NOT NULL DEFAULT 0,
    portrait_frame INTEGER NOT NULL DEFAULT 0,
    hardcore_death_league TEXT,
    hardcore_death_utc TEXT,
    hardcore_death_permanent INTEGER CHECK (hardcore_death_permanent IN (0, 1))
) STRICT;
INSERT INTO account_characters (id, account_id, name, class, level, league, area_id, created_utc, last_played_utc,
    last_town, portrait, portrait_frame, hardcore_death_league, hardcore_death_utc, hardcore_death_permanent)
SELECT id, account_id, name, json_extract(character, '$.Class'), json_extract(character, '$.Level'),
    json_extract(character, '$.League'), json_extract(character, '$.AreaId'), json_extract(character, '$.CreatedUtc'),
    json_extract(character, '$.LastPlayedUtc'), json_extract(character, '$.LastTown'),
    coalesce(json_extract(character, '$.Portrait'), 0), coalesce(json_extract(character, '$.PortraitFrame'), 0),
    json_extract(character, '$.HardcoreDeath.SourceLeague'), json_extract(character, '$.HardcoreDeath.AtUtc'),
    json_extract(character, '$.HardcoreDeath.PermanentlyDead')
FROM account_characters_json;
