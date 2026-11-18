-- Preserve the native RelicItemEffectVariations row across character and stash saves.
ALTER TABLE items ADD COLUMN relic_style INTEGER CHECK (relic_style BETWEEN 0 AND 255);
