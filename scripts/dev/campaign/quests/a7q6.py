"""a7q6 "Kishara's Star" (act7.md): Weylam's quest, Kishara's Lockbox in The Causeway's city, the star handed in,
Weylam's book (offer 50: 1 passive, 2 respec)."""
QUEST = "a7q6"
DONE = "a7q6-UsedRewardBook"
REQUIRES = ["a7q1"]

STEPS = [
    {"verb": "talk", "npc": "Act7/Weylam", "option": "Kishara's Star", "area": "2_7_town", "expect_flag": "a7q6-QuestReceived", "skip_if": ["a7q6-QuestReceived", "a7q6-HaveItem", "a7q6-DeliveredItem"]},
    {"verb": "goto", "area": "2_7_10", "skip_if": ["a7q6-HaveItem", "a7q6-DeliveredItem"]},
    {"verb": "interact", "target": "KisharaStarChest", "area": "2_7_10", "skip_if": ["a7q6-HaveItem", "a7q6-DeliveredItem"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/KisharaStar", "expect_flag": "a7q6-HaveItem", "skip_if": ["a7q6-HaveItem", "a7q6-DeliveredItem"]},
    {"verb": "talk", "npc": "Act7/Weylam", "option": "Kishara's Star", "area": "2_7_town", "expect_flag": "a7q6-DeliveredItem", "skip_if": ["a7q6-DeliveredItem", "a7q6-HaveRewardBook"]},
    {"verb": "reward", "npc": "Act7/Weylam", "option": "Kishara's Star Reward", "area": "2_7_town", "skip_if": "a7q6-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act7/Book-a7q6", "expect_flag": "a7q6-UsedRewardBook"},
    {"verb": "passives"},
]
