-- Divination card rewards whose name lookup found the wrong base: the first BaseItemTypes row with the name is a
-- trade proxy (Boundless Realms' "Map") or a legacy base (Lingering Remnants' Vaal Temple Map, Last Hope's Mortal Hope,
-- Divine Shard's Divine Vessel). Explicit members name the current bases; a card with members takes one of them.
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier1', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier2', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier3', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier4', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier5', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier6', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier7', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier8', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier9', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier10', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier11', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier12', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier13', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier14', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier15', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardBoundlessRealms', 'base', 'Metadata/Items/Maps/MapKeyTier16', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardLingeringRemnants', 'base', 'Metadata/Items/Maps/MapKeyVaalTemple', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardLastHope', 'base', 'Metadata/Items/MapFragments/CurrencyVaalFragment2_2', '', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardDivineShard', 'base', 'Metadata/Items/MapFragments/CurrencyFragmentPantheonFlask', '', 1);
-- Her Mask and Sambodhi's Vow listed both the current stackable fragments and their legacy bases.
DELETE FROM divination_reward_members
WHERE card_id IN ('Metadata/Items/DivinationCards/DivinationCardHerMask', 'Metadata/Items/DivinationCards/DivinationCardSambodhisVow')
  AND member_kind = 'base' AND member_id LIKE 'Metadata/Items/MapFragments/VaalFragment%';
-- Precursor's Emblem has seven ring bases; the name lookup always took the first definition.
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardRemembrance', 'unique', 'Precursor''s Emblem', 'Metadata/Items/Rings/Ring5', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardRemembrance', 'unique', 'Precursor''s Emblem', 'Metadata/Items/Rings/Ring6', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardRemembrance', 'unique', 'Precursor''s Emblem', 'Metadata/Items/Rings/Ring7', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardRemembrance', 'unique', 'Precursor''s Emblem', 'Metadata/Items/Rings/Ring8', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardRemembrance', 'unique', 'Precursor''s Emblem', 'Metadata/Items/Rings/Ring12', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardRemembrance', 'unique', 'Precursor''s Emblem', 'Metadata/Items/Rings/Ring13', 1);
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardRemembrance', 'unique', 'Precursor''s Emblem', 'Metadata/Items/Rings/Ring14', 1);
-- Crown of Eyes is a Hubris Circlet; the Steel Circlet definition is the legacy one.
INSERT INTO divination_reward_members VALUES ('Metadata/Items/DivinationCards/DivinationCardTheWatcher', 'unique', 'Crown of Eyes', 'Metadata/Items/Armours/Helmets/HelmetInt11', 1);
