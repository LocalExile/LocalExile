"""a7q8 "In Memory of Greust" (act7.md): Helena's Greust's Necklace (offer 52, a quest item) placed at the Azmeri
shrine in The Northern Forest (a7q8-DeliveredItem), Helena's reward (offer 51, amulets)."""
QUEST = "a7q8"
DONE = "a7q8-QuestRewardTaken"
REQUIRES = ["a7q1"]

STEPS = [
    {"verb": "talk", "npc": "Act7/Helena", "option": "Greust's Necklace", "area": "2_7_town", "expect_flag": "a7q8-QuestReceived", "skip_if": ["a7q8-QuestReceived", "a7q8-DeliveredItem"]},
    {"verb": "reward", "npc": "Act7/Helena", "option": "Take Greust's Necklace", "area": "2_7_town", "pick": ["Greust's Necklace"], "skip_if": ["a7q8-HaveItem", "a7q8-DeliveredItem"]},
    {"verb": "goto", "area": "2_7_8", "skip_if": "a7q8-DeliveredItem"},
    {"verb": "interact", "target": "GreustShrine", "area": "2_7_8", "expect_flag": "a7q8-DeliveredItem", "skip_if": "a7q8-DeliveredItem"},
    {"verb": "talk", "npc": "Act7/Helena", "option": "Greust's Necklace", "area": "2_7_town", "expect_flag": "a7q8-Congratulated", "skip_if": "a7q8-Congratulated"},
    {"verb": "reward", "npc": "Act7/Helena", "option": "Greust's Necklace Reward", "area": "2_7_town", "pick": ["Amulet"]},
]
