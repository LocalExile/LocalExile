"""a10q4 "No Love for Old Ghosts" (act10.md): Weylam's quest; the Sealed Chest in The Ossuary holds the Elixir of
Allure; handed in, Weylam's Book of Regrets (offer 67: 2 respec)."""
QUEST = "a10q4"
DONE = "a10q4-UsedRewardBook"
REQUIRES = ["a10q1"]

STEPS = [
    {"verb": "talk", "npc": "Act10/Weylam", "option": "Elixir of Allure", "area": "2_10_town", "expect_flag": "a10q4-QuestReceived", "skip_if": ["a10q4-QuestReceived", "a10q4-HaveItem", "a10q4-DeliveredItem"]},
    {"verb": "goto", "area": "2_10_9", "skip_if": ["a10q4-HaveItem", "a10q4-DeliveredItem"]},
    {"verb": "interact", "target": "QuestChests/Potion", "area": "2_10_9", "skip_if": ["a10q4-HaveItem", "a10q4-DeliveredItem"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act10/Potion", "expect_flag": "a10q4-HaveItem", "skip_if": ["a10q4-HaveItem", "a10q4-DeliveredItem"]},
    {"verb": "talk", "npc": "Act10/Weylam", "option": "Elixir of Allure", "area": "2_10_town", "expect_flag": "a10q4-DeliveredItem", "skip_if": ["a10q4-DeliveredItem", "a10q4-HaveRewardBook"]},
    {"verb": "reward", "npc": "Act10/Weylam", "option": "Elixir of Allure Reward", "area": "2_10_town", "skip_if": "a10q4-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act10/Book-a10q4", "expect_flag": "a10q4-UsedRewardBook"},
]
