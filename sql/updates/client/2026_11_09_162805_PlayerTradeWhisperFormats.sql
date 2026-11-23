-- Client table PlayerTradeWhisperFormats: all 4 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "PlayerTradeWhisperFormats" ("_row", "Id", "Whisper", "InStash", "IsPriced") VALUES
(0, 'ItemInLeague', 'Hi, I would like to buy your {{ITEM}} in {{LEAGUE}}', 0, 0),
(1, 'ItemInLeaguePriced', 'Hi, I would like to buy your {{ITEM}} listed for {{PRICE}} in {{LEAGUE}}', 0, 1),
(2, 'ItemInLeagueInStash', 'Hi, I would like to buy your {{ITEM}} in {{LEAGUE}} (stash tab "{{STASH}}"; position: left {{LEFT}}, top {{TOP}})', 1, 0),
(3, 'ItemInLeaguePricedInStash', 'Hi, I would like to buy your {{ITEM}} listed for {{PRICE}} in {{LEAGUE}} (stash tab "{{STASH}}"; position: left {{LEFT}}, top {{TOP}})', 1, 1);
