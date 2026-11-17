-- Ordinary currency that was missing from the core drop pool. Each drop_level is the client's BaseItemTypes.DropLevel.
-- No weight for these is published (Agents/docs/game/currency-drops.md): all are estimates placed by rarity against
-- the existing rows.
INSERT INTO drop_currency_weights (currency_id, weight, drop_level, enabled, source, confidence, notes) VALUES
('Metadata/Items/Currency/CurrencyAtlasPassiveRefund', 25.0, 70, 1, 'core', 'estimated', 'Orb of Unmaking; same weight as Orb of Regret (project owner''s judgment)'),
('Metadata/Items/Currency/CurrencyHinekorasLock', 0.01, 40, 1, 'core', 'estimated', 'Hinekora''s Lock; same weight as Mirror of Kalandra'),
('Metadata/Items/Currency/CurrencyUpgradeToRareAndSetSockets', 18.0, 25, 1, 'core', 'estimated', 'Orb of Binding; uncommon, between Blessed (15) and Regal (20)'),
('Metadata/Items/Currency/CurrencyStrongboxQuality', 17.0, 1, 1, 'core', 'estimated', 'Engineer''s Orb; uncommon, between Blessed (15) and Regal (20)'),
('Metadata/Items/Currency/CurrencyInstillingOrb', 16.0, 40, 1, 'core', 'estimated', 'Instilling Orb; uncommon, between Blessed (15) and Regal (20)'),
('Metadata/Items/Currency/CurrencyEnkindlingOrb', 16.0, 40, 1, 'core', 'estimated', 'Enkindling Orb; uncommon, between Blessed (15) and Regal (20)'),
('Metadata/Items/Currency/CurrencyMapQuality', 35.0, 52, 1, 'core', 'estimated', 'Cartographer''s Chisel; fairly common in its level range, near Chaos (40) and Scouring (30)'),
('Metadata/Items/Currency/CurrencyRerollUnique', 2.0, 35, 1, 'core', 'estimated', 'Ancient Orb; core drop since 3.28, rare, same as Exalted'),
('Metadata/Items/Currency/CurrencyUpgradeMapTier', 1.5, 1, 1, 'core', 'estimated', 'Harbinger''s Orb; rare, same as Orb of Annulment'),
('Metadata/Items/DivinationCards/DivinationCardDeck', 2.0, 50, 1, 'core', 'estimated', 'Stacked Deck; rare outside league mechanics, same as Exalted'),
('Metadata/Items/Currency/CurrencyRerollDefences', 0.3, 35, 1, 'core', 'estimated', 'Sacred Orb; very rare, well below Divine (1)'),
('Metadata/Items/Currency/CurrencyFractureRare', 0.2, 68, 1, 'core', 'estimated', 'Fracturing Orb; core drop since 3.28, very rare, well below Divine (1)');
