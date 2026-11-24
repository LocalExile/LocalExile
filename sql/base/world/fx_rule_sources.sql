-- What makes a rule exist for an actor. Any one row is enough. kind: stat (the actor has the stat, from any item,
-- flask, passive, buff or area), item (a base type or a unique's name), currency (the item being used), area (an
-- area or its modifier), buff, skill, always. key: the stat, item, currency, area, buff or skill; empty for always.
CREATE TABLE fx_rule_sources (
    rule TEXT NOT NULL,
    ord INTEGER NOT NULL DEFAULT 0,
    kind TEXT NOT NULL CHECK (kind IN ('stat', 'item', 'currency', 'area', 'buff', 'skill', 'always')),
    key TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (rule, ord)
) STRICT;
CREATE INDEX fx_rule_sources_key ON fx_rule_sources (kind, key);
