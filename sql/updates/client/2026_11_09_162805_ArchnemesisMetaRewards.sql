-- Client table ArchnemesisMetaRewards: all 11 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "ArchnemesisMetaRewards" ("_row", "Id", "RewardText", "RewardGroup", "ScriptArgument", "MinLevel", "MaxLevel") VALUES
(0, 'AdditionalRewards1', 'All Reward Types have an additional reward', 0, '1', 1, 100),
(1, 'RewardsMultiplier2', 'Rewards are doubled', 1, '2', 1, 100),
(2, 'RewardTypeOverrideDivination', 'All Reward Types are Divination Cards', 2, 'DivinationCards', 46, 100),
(3, 'RewardTypeOverrideUniques', 'All Reward Types are Uniques', 2, 'Uniques', 1, 100),
(4, 'RewardTypeOverrideCurrency', 'All Reward Types are Currency', 2, 'Currency', 1, 100),
(5, 'MinionsHaveRewards', 'Monster''s Minions drop a randomly-chosen Reward Type', 3, '', 1, 100),
(6, 'BetterRewards1', 'Rewards are rolled 1 additional time, choosing the rarest result', 4, '1', 1, 100),
(7, 'BetterRewards2', 'Rewards are rolled 2 additional times, choosing the rarest result', 4, '2', 1, 100),
(8, 'BetterRewards4', 'Rewards are rolled 4 additional times, choosing the rarest result', 4, '4', 1, 100),
(9, 'BetterRewards8', 'Rewards are rolled 6 additional times, choosing the rarest result', 4, '6', 1, 100),
(10, 'CorruptedRewards', 'Items dropped from the Monster and its Minions are Corrupted', 5, '', 1, 100);
