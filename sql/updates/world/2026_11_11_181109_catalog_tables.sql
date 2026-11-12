-- The item and jewel catalogues as typed tables instead of catalog_entries' JSON payloads. A list becomes a child
-- table keyed by its entry and the item's place in the list (ord). An empty list and an absent one are the same to the
-- Gateway, except a unique's implicits: own_implicits says the unique replaces its base type's implicits with its rows
-- of unique_item_mods where implicit = 1, which may be none. The rows are moved over, then catalog_entries goes.

CREATE TABLE unique_items (
    name TEXT NOT NULL,
    base_type TEXT NOT NULL,
    item_level INTEGER NOT NULL DEFAULT 86,
    corrupted INTEGER NOT NULL DEFAULT 0 CHECK (corrupted IN (0, 1)),
    own_implicits INTEGER NOT NULL DEFAULT 0 CHECK (own_implicits IN (0, 1)),
    mirrored INTEGER NOT NULL DEFAULT 0 CHECK (mirrored IN (0, 1)),
    influences INTEGER NOT NULL DEFAULT 0,
    fractured INTEGER NOT NULL DEFAULT 0,
    encounter_only INTEGER NOT NULL DEFAULT 0 CHECK (encounter_only IN (0, 1)),
    jewel_limit INTEGER NOT NULL DEFAULT 0,
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    PRIMARY KEY (name, base_type)
) STRICT;

-- A unique's explicit mods (implicit = 0) and, with own_implicits, the implicits that replace its base type's.
CREATE TABLE unique_item_mods (
    name TEXT NOT NULL,
    base_type TEXT NOT NULL,
    implicit INTEGER NOT NULL CHECK (implicit IN (0, 1)),
    ord INTEGER NOT NULL,
    mod TEXT NOT NULL,
    PRIMARY KEY (name, base_type, implicit, ord),
    FOREIGN KEY (name, base_type) REFERENCES unique_items (name, base_type) ON DELETE CASCADE
) STRICT;

-- A unique's roll of one stat of a mod where it differs from the Mods row.
CREATE TABLE unique_item_ranges (
    name TEXT NOT NULL,
    base_type TEXT NOT NULL,
    ord INTEGER NOT NULL,
    mod TEXT NOT NULL,
    stat INTEGER NOT NULL,
    min INTEGER NOT NULL,
    max INTEGER NOT NULL,
    implicit INTEGER NOT NULL DEFAULT 0 CHECK (implicit IN (0, 1)),
    PRIMARY KEY (name, base_type, ord),
    FOREIGN KEY (name, base_type) REFERENCES unique_items (name, base_type) ON DELETE CASCADE
) STRICT;

-- A pool the unique rolls count of its options from (high_level_count of them from item level high_level).
CREATE TABLE unique_item_roll_groups (
    name TEXT NOT NULL,
    base_type TEXT NOT NULL,
    grp INTEGER NOT NULL,
    count INTEGER NOT NULL,
    high_level INTEGER,
    high_level_count INTEGER,
    PRIMARY KEY (name, base_type, grp),
    FOREIGN KEY (name, base_type) REFERENCES unique_items (name, base_type) ON DELETE CASCADE
) STRICT;

CREATE TABLE unique_item_roll_options (
    name TEXT NOT NULL,
    base_type TEXT NOT NULL,
    grp INTEGER NOT NULL,
    ord INTEGER NOT NULL,
    mod TEXT NOT NULL,
    PRIMARY KEY (name, base_type, grp, ord),
    FOREIGN KEY (name, base_type, grp) REFERENCES unique_item_roll_groups (name, base_type, grp) ON DELETE CASCADE
) STRICT;

-- The jewels that change passives in their radius (kind radius) or need a number of them allocated (kind threshold).
CREATE TABLE radius_jewels (
    kind TEXT NOT NULL CHECK (kind IN ('radius', 'threshold')),
    name TEXT NOT NULL,
    base TEXT NOT NULL,
    conversion_mod TEXT NOT NULL,
    jewel_limit INTEGER NOT NULL DEFAULT 0,
    level INTEGER NOT NULL DEFAULT 1,
    corrupted INTEGER NOT NULL DEFAULT 0 CHECK (corrupted IN (0, 1)),
    radius INTEGER NOT NULL DEFAULT 0,
    variant_keeps_conversion_mod INTEGER NOT NULL DEFAULT 0 CHECK (variant_keeps_conversion_mod IN (0, 1)),
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    PRIMARY KEY (kind, name, base)
) STRICT;

-- A radius jewel's global mods and its extra conversion mods.
CREATE TABLE radius_jewel_mods (
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    base TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('global', 'extra_conversion')),
    ord INTEGER NOT NULL,
    mod TEXT NOT NULL,
    PRIMARY KEY (kind, name, base, role, ord),
    FOREIGN KEY (kind, name, base) REFERENCES radius_jewels (kind, name, base) ON DELETE CASCADE
) STRICT;

CREATE TABLE radius_jewel_variants (
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    base TEXT NOT NULL,
    ord INTEGER NOT NULL,
    variant TEXT NOT NULL,
    mod_id TEXT NOT NULL,
    value INTEGER,
    PRIMARY KEY (kind, name, base, ord),
    FOREIGN KEY (kind, name, base) REFERENCES radius_jewels (kind, name, base) ON DELETE CASCADE
) STRICT;

-- The other unique jewels with server-side effects: the global ones and the special ones.
CREATE TABLE unique_jewels (
    kind TEXT NOT NULL CHECK (kind IN ('global', 'special')),
    name TEXT NOT NULL,
    base TEXT NOT NULL,
    corrupted INTEGER NOT NULL DEFAULT 0 CHECK (corrupted IN (0, 1)),
    level INTEGER NOT NULL DEFAULT 1,
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    PRIMARY KEY (kind, name)
) STRICT;

-- A unique jewel's mods, each with its fixed value (null: the mod's roll).
CREATE TABLE unique_jewel_mods (
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    ord INTEGER NOT NULL,
    mod TEXT NOT NULL,
    value INTEGER,
    PRIMARY KEY (kind, name, ord),
    FOREIGN KEY (kind, name) REFERENCES unique_jewels (kind, name) ON DELETE CASCADE
) STRICT;

CREATE TABLE timeless_jewels (
    name TEXT NOT NULL PRIMARY KEY,
    tree_version INTEGER NOT NULL,
    faction TEXT NOT NULL,
    mod_id TEXT NOT NULL,
    min_seed INTEGER NOT NULL,
    max_seed INTEGER NOT NULL,
    display_multiplier INTEGER NOT NULL DEFAULT 1,
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1))
) STRICT;

CREATE TABLE timeless_jewel_conquerors (
    jewel TEXT NOT NULL REFERENCES timeless_jewels (name) ON DELETE CASCADE,
    ord INTEGER NOT NULL,
    name TEXT NOT NULL,
    conqueror_index INTEGER NOT NULL,
    version INTEGER NOT NULL,
    PRIMARY KEY (jewel, ord)
) STRICT;

CREATE TABLE tattoo_effects (
    name TEXT NOT NULL PRIMARY KEY,
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1))
) STRICT;

CREATE TABLE tattoo_effect_stats (
    tattoo TEXT NOT NULL REFERENCES tattoo_effects (name) ON DELETE CASCADE,
    ord INTEGER NOT NULL,
    stat TEXT NOT NULL,
    value INTEGER NOT NULL,
    PRIMARY KEY (tattoo, ord)
) STRICT;

-- The jewel art of a unique jewel by its name: the hash of its visual identity.
CREATE TABLE jewel_visuals (
    name TEXT NOT NULL PRIMARY KEY,
    hash INTEGER NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1))
) STRICT;

INSERT INTO unique_items (name, base_type, item_level, corrupted, own_implicits, mirrored, influences, fractured,
    encounter_only, jewel_limit, enabled)
SELECT json_extract(payload, '$.Name'), json_extract(payload, '$.BaseTypeId'),
    coalesce(json_extract(payload, '$.ItemLevel'), 86), coalesce(json_extract(payload, '$.Corrupted'), 0),
    coalesce(json_type(payload, '$.Implicits') = 'array', 0), coalesce(json_extract(payload, '$.Mirrored'), 0),
    coalesce(json_extract(payload, '$.Influences'), 0), coalesce(json_extract(payload, '$.Fractured'), 0),
    coalesce(json_extract(payload, '$.EncounterOnly'), 0), coalesce(json_extract(payload, '$.JewelLimit'), 0), enabled
FROM catalog_entries WHERE kind = 'unique_item';

INSERT INTO unique_item_mods (name, base_type, implicit, ord, mod)
SELECT json_extract(c.payload, '$.Name'), json_extract(c.payload, '$.BaseTypeId'), 0, m.key, m.value
FROM catalog_entries c, json_each(c.payload, '$.Mods') m
WHERE c.kind = 'unique_item' AND json_type(c.payload, '$.Mods') = 'array'
UNION ALL
SELECT json_extract(c.payload, '$.Name'), json_extract(c.payload, '$.BaseTypeId'), 1, m.key, m.value
FROM catalog_entries c, json_each(c.payload, '$.Implicits') m
WHERE c.kind = 'unique_item' AND json_type(c.payload, '$.Implicits') = 'array';

INSERT INTO unique_item_ranges (name, base_type, ord, mod, stat, min, max, implicit)
SELECT json_extract(c.payload, '$.Name'), json_extract(c.payload, '$.BaseTypeId'), r.key,
    json_extract(r.value, '$.Mod'), json_extract(r.value, '$.Stat'), json_extract(r.value, '$.Min'),
    json_extract(r.value, '$.Max'), coalesce(json_extract(r.value, '$.Implicit'), 0)
FROM catalog_entries c, json_each(c.payload, '$.Ranges') r
WHERE c.kind = 'unique_item' AND json_type(c.payload, '$.Ranges') = 'array';

INSERT INTO unique_item_roll_groups (name, base_type, grp, count, high_level, high_level_count)
SELECT json_extract(c.payload, '$.Name'), json_extract(c.payload, '$.BaseTypeId'), g.key,
    json_extract(g.value, '$.Count'), json_extract(g.value, '$.HighLevel'), json_extract(g.value, '$.HighLevelCount')
FROM catalog_entries c, json_each(c.payload, '$.RollGroups') g
WHERE c.kind = 'unique_item' AND json_type(c.payload, '$.RollGroups') = 'array';

INSERT INTO unique_item_roll_options (name, base_type, grp, ord, mod)
SELECT json_extract(c.payload, '$.Name'), json_extract(c.payload, '$.BaseTypeId'), g.key, o.key, o.value
FROM catalog_entries c, json_each(c.payload, '$.RollGroups') g, json_each(g.value, '$.Options') o
WHERE c.kind = 'unique_item' AND json_type(c.payload, '$.RollGroups') = 'array';

INSERT INTO radius_jewels (kind, name, base, conversion_mod, jewel_limit, level, corrupted, radius,
    variant_keeps_conversion_mod, enabled)
SELECT CASE kind WHEN 'radius_jewel' THEN 'radius' ELSE 'threshold' END, json_extract(payload, '$.Name'),
    json_extract(payload, '$.Base'), json_extract(payload, '$.ConversionMod'), coalesce(json_extract(payload, '$.Limit'), 0),
    coalesce(json_extract(payload, '$.Level'), 1), coalesce(json_extract(payload, '$.Corrupted'), 0),
    coalesce(json_extract(payload, '$.Radius'), 0), coalesce(json_extract(payload, '$.VariantKeepsConversionMod'), 0),
    enabled
FROM catalog_entries WHERE kind IN ('radius_jewel', 'threshold_jewel');

INSERT INTO radius_jewel_mods (kind, name, base, role, ord, mod)
SELECT CASE c.kind WHEN 'radius_jewel' THEN 'radius' ELSE 'threshold' END, json_extract(c.payload, '$.Name'),
    json_extract(c.payload, '$.Base'), 'global', m.key, m.value
FROM catalog_entries c, json_each(c.payload, '$.GlobalMods') m
WHERE c.kind IN ('radius_jewel', 'threshold_jewel') AND json_type(c.payload, '$.GlobalMods') = 'array'
UNION ALL
SELECT CASE c.kind WHEN 'radius_jewel' THEN 'radius' ELSE 'threshold' END, json_extract(c.payload, '$.Name'),
    json_extract(c.payload, '$.Base'), 'extra_conversion', m.key, m.value
FROM catalog_entries c, json_each(c.payload, '$.ExtraConversionMods') m
WHERE c.kind IN ('radius_jewel', 'threshold_jewel') AND json_type(c.payload, '$.ExtraConversionMods') = 'array';

INSERT INTO radius_jewel_variants (kind, name, base, ord, variant, mod_id, value)
SELECT CASE c.kind WHEN 'radius_jewel' THEN 'radius' ELSE 'threshold' END, json_extract(c.payload, '$.Name'),
    json_extract(c.payload, '$.Base'), v.key, json_extract(v.value, '$.Name'), json_extract(v.value, '$.ModId'),
    json_extract(v.value, '$.Value')
FROM catalog_entries c, json_each(c.payload, '$.Variants') v
WHERE c.kind IN ('radius_jewel', 'threshold_jewel') AND json_type(c.payload, '$.Variants') = 'array';

INSERT INTO unique_jewels (kind, name, base, corrupted, level, enabled)
SELECT CASE kind WHEN 'unique_jewel_global' THEN 'global' ELSE 'special' END, json_extract(payload, '$.Name'),
    json_extract(payload, '$.Base'), coalesce(json_extract(payload, '$.Corrupted'), 0),
    coalesce(json_extract(payload, '$.Level'), 1), enabled
FROM catalog_entries WHERE kind IN ('unique_jewel_global', 'unique_jewel_special');

INSERT INTO unique_jewel_mods (kind, name, ord, mod, value)
SELECT CASE c.kind WHEN 'unique_jewel_global' THEN 'global' ELSE 'special' END, json_extract(c.payload, '$.Name'),
    m.key, m.value, json_extract(c.payload, '$.Values[' || m.key || ']')
FROM catalog_entries c, json_each(c.payload, '$.Mods') m
WHERE c.kind IN ('unique_jewel_global', 'unique_jewel_special') AND json_type(c.payload, '$.Mods') = 'array';

INSERT INTO timeless_jewels (name, tree_version, faction, mod_id, min_seed, max_seed, display_multiplier, enabled)
SELECT json_extract(payload, '$.Name'), json_extract(payload, '$.TreeVersion'), json_extract(payload, '$.Faction'),
    json_extract(payload, '$.ModId'), json_extract(payload, '$.MinSeed'), json_extract(payload, '$.MaxSeed'),
    coalesce(json_extract(payload, '$.DisplayMultiplier'), 1), enabled
FROM catalog_entries WHERE kind = 'timeless_jewel';

INSERT INTO timeless_jewel_conquerors (jewel, ord, name, conqueror_index, version)
SELECT json_extract(c.payload, '$.Name'), q.key, json_extract(q.value, '$.Name'), json_extract(q.value, '$.Index'),
    json_extract(q.value, '$.Version')
FROM catalog_entries c, json_each(c.payload, '$.Conquerors') q
WHERE c.kind = 'timeless_jewel' AND json_type(c.payload, '$.Conquerors') = 'array';

INSERT INTO tattoo_effects (name, enabled)
SELECT json_extract(payload, '$.Name'), enabled FROM catalog_entries WHERE kind = 'tattoo_effect';

INSERT INTO tattoo_effect_stats (tattoo, ord, stat, value)
SELECT json_extract(c.payload, '$.Name'), s.key, json_extract(s.value, '$.Stat'), json_extract(s.value, '$.Value')
FROM catalog_entries c, json_each(c.payload, '$.Stats') s
WHERE c.kind = 'tattoo_effect' AND json_type(c.payload, '$.Stats') = 'array';

INSERT INTO jewel_visuals (name, hash, enabled)
SELECT json_extract(payload, '$.Name'), json_extract(payload, '$.Hash'), enabled
FROM catalog_entries WHERE kind = 'unique_jewel_visual';

DROP TABLE catalog_entries;
