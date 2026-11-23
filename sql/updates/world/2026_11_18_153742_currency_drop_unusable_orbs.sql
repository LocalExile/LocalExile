-- Engineer's Orb and Harbinger's Orb are no longer usable: the client's CurrencyItems gives both the Directions
-- "This item is no longer usable." with an empty Action (confirmed in the 3.29.3d client, where a right-click does not
-- start targeting). Disabled rather than deleted so the decision stays recorded; ItemDrops.Build skips enabled = 0.
UPDATE drop_currency_weights SET enabled = 0,
    notes = 'Engineer''s Orb; disabled: the client marks it "This item is no longer usable." (CurrencyItems.Directions, empty Action)'
WHERE currency_id = 'Metadata/Items/Currency/CurrencyStrongboxQuality';
UPDATE drop_currency_weights SET enabled = 0,
    notes = 'Harbinger''s Orb; disabled: the client marks it "This item is no longer usable." (CurrencyItems.Directions, empty Action)'
WHERE currency_id = 'Metadata/Items/Currency/CurrencyUpgradeMapTier';
