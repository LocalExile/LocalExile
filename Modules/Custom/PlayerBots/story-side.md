# Side quests (bot guide)

The optional quests (passive points, rewards, Pantheon gods). A bot does each one when its act comes up, after the main quests before it in the same act. Source: the quests of the game data (`data/tables/Quest.json`, the client's own Quest.dat) in the usual levelling route through each act, as the project's campaign driver plays it (`scripts/dev/campaign/order.py`).

The bots read the table below in order: one row is one quest. `Town` is the WorldAreas id of the act town the bot
returns to between quests (and where players see it). Do not change the column order.

| Act | Town | Quest | Name | Where, boss / NPC |
|---|---|---|---|---|
| Act 1 (Lioneye's Watch) | 1_1_town | a1q8 | A Dirty Job | The Fetid Pool; Tarkleigh |
| Act 1 (Lioneye's Watch) | 1_1_town | a1q7 | The Dweller of the Deep | The Flooded Depths; Tarkleigh |
| Act 1 (Lioneye's Watch) | 1_1_town | a1q6 | The Marooned Mariner | Fairgraves, The Ship Graveyard; Allflame |
| Act 2 (The Forest Encampment) | 1_2_town | a2q10 | The Great White Beast | The Den, off The Old Fields |
| Act 2 (The Forest Encampment) | 1_2_town | a2q5 | Through Sacred Ground | Golden Hand, The Crypt; Yeena |
| Act 3 (The Sarn Encampment) | 1_3_town | a3q11 | Victario's Secrets | busts in The Sewers; Hargan |
| Act 3 (The Sarn Encampment) | 1_3_town | a3q13 | A Swig of Hope | Chitus' Plum / Decanter Spiritus, The Docks |
| Act 3 (The Sarn Encampment) | 1_3_town | a3q12 | A Fixture of Fate | Golden Pages, The Archives; Siosa |
| Act 4 (Highgate) | 1_4_town | a4q6 | An Indomitable Spirit | Deshret's spirit, The Mines; Tasuni |
| Act 5 (Overseer's Tower) | 1_5_town | a5q3 | In Service to Science | Miasmeter, The Control Blocks; Vilenta |
| Act 5 (Overseer's Tower) | 1_5_town | a5q7 | Kitava's Torments | The Reliquary; Lani |
| Act 6 (Lioneye's Watch) | 2_6_town | a6q4 | Fallen from Grace | clear The Twilight Strand; Lilly |
| Act 6 (Lioneye's Watch) | 2_6_town | a6q5 | Bestel's Epic | Manuscript, The Tidal Island |
| Act 6 (Lioneye's Watch) | 2_6_town | a6q7 | The Cloven One | Abberath, The Prisoner's Gate |
| Act 6 (Lioneye's Watch) | 2_6_town | a6q6 | The Puppet Mistress | Ryslatha, The Wetlands |
| Act 7 (The Bridge Encampment) | 2_7_town | a7q5 | The Silver Locket | The Broken Bridge; Weylam |
| Act 7 (The Bridge Encampment) | 2_7_town | a7q8 | In Memory of Greust | Azmeri shrine, The Northern Forest |
| Act 7 (The Bridge Encampment) | 2_7_town | a7q9 | Queen of Despair | Gruthkul, The Dread Thicket |
| Act 7 (The Bridge Encampment) | 2_7_town | a7q6 | Kishara's Star | The Causeway; Weylam |
| Act 8 (The Sarn Encampment) | 2_8_town | a8q5 | The Wings of Vastiri | Hector Titucius, The Bath House; Hargan |
| Act 8 (The Sarn Encampment) | 2_8_town | a8q4 | Reflection of Terror | Yugul, The High Gardens |
| Act 8 (The Sarn Encampment) | 2_8_town | a8q6 | Love is Dead | Ankh of Eternity, The Quay; Clarissa |
| Act 8 (The Sarn Encampment) | 2_8_town | a8q7 | The Gemling Legion | The Grain Gate; Maramoa |
| Act 9 (Highgate) | 2_9_town | a9q3 | The Storm Blade | The Vastiri Desert; Petarus and Vanja |
| Act 9 (Highgate) | 2_9_town | a9q5 | Queen of the Sands | Shakari, The Oasis |
| Act 9 (Highgate) | 2_9_town | a9q4 | Fastis Fortuna | Calendar of Fortune, The Foothills |
| Act 9 (Highgate) | 2_9_town | a9q2 | The Ruler of Highgate | Kira/Garukhan, The Quarry |
| Act 10 (Oriath Docks) | 2_10_town | a10q6 | Vilenta's Vengeance | Vilenta, The Control Blocks; Lani |
| Act 10 (Oriath Docks) | 2_10_town | a10q4 | No Love for Old Ghosts | Elixir of Allure, The Ossuary; Weylam |
| Act 10 (Oriath Docks) | 2_10_town | a10q5 | Map to Tsoatha | Teardrop, The Reliquary; Lilly |
