"""a7q5 "The Silver Locket" (act7.md): Weylam's Broken Bridge talk and quest, the Dirty Lockbox in The Broken
Bridge's manor, the locket handed in, a flask from offer 49."""
QUEST = "a7q5"
DONE = "a7q5-QuestRewardTaken"
REQUIRES = []

STEPS = [
    {"verb": "goto", "area": "2_7_town"},
    {"verb": "talk", "npc": "Act7/Weylam", "option": "The Broken Bridge", "area": "2_7_town", "skip_if": ["WeylamOnBrokenBridge", "a7q5-QuestReceived"]},
    {"verb": "talk", "npc": "Act7/Weylam", "option": "The Silver Locket", "area": "2_7_town", "expect_flag": "a7q5-QuestReceived", "skip_if": ["a7q5-QuestReceived", "a7q5-HaveItem", "a7q5-DeliveredItem"]},
    {"verb": "goto", "area": "2_7_1", "skip_if": ["a7q5-HaveItem", "a7q5-DeliveredItem"]},
    {"verb": "interact", "target": "LocketChest", "area": "2_7_1", "skip_if": ["a7q5-HaveItem", "a7q5-DeliveredItem"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/SilverLocket", "expect_flag": "a7q5-HaveItem", "skip_if": ["a7q5-HaveItem", "a7q5-DeliveredItem"]},
    {"verb": "talk", "npc": "Act7/Weylam", "option": "The Silver Locket", "area": "2_7_town", "expect_flag": "a7q5-DeliveredItem", "skip_if": "a7q5-DeliveredItem"},
    {"verb": "reward", "npc": "Act7/Weylam", "option": "Silver Locket Reward", "area": "2_7_town", "pick": ["Quartz Flask", "Flask"]},
]
