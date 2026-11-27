-- The effect rules' engine has more rule sources than main's world rules: an item (a base type or a unique's name), a
-- currency being used, an area or its modifier, a buff, and always (no key). The table main's update created allows
-- stat and skill only, so it is rebuilt with the wider kind list (same columns, same foreign key, rows kept), before the
-- rules that use those kinds. kind: stat (the actor has the stat, from any item, flask, passive, buff or area), item,
-- currency, area, buff, skill, always. key: the stat, item, currency, area, buff or skill; empty for always.
CREATE TABLE fx_rule_sources_wide (
    rule TEXT NOT NULL REFERENCES fx_rules(id) ON DELETE CASCADE,
    ord INTEGER NOT NULL DEFAULT 0,
    kind TEXT NOT NULL CHECK (kind IN ('stat', 'item', 'currency', 'area', 'buff', 'skill', 'always')),
    key TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (rule, ord)
) STRICT;

INSERT INTO fx_rule_sources_wide (rule, ord, kind, key) SELECT rule, ord, kind, key FROM fx_rule_sources;

DROP TABLE fx_rule_sources;

ALTER TABLE fx_rule_sources_wide RENAME TO fx_rule_sources;

CREATE INDEX fx_rule_sources_key ON fx_rule_sources (kind, key);
