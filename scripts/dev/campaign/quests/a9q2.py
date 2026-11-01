"""a9q2 "The Ruler of Highgate" (act9.md): Irasha's "Oyun" (a9q2-QuestReceived); Garukhan dies in The Quarry and drops
the Sekhema Feather, Sin (1380: the Pantheon panel); the feather goes to Irasha (IrashaTakeFeather), her book (offer
59)."""
QUEST = "a9q2"
DONE = "a9q2-UsedRewardBook"
REQUIRES = ["a9q3"]

STEPS = [
    {"verb": "talk", "npc": "Irasha", "option": "Oyun", "area": "2_9_town", "expect_flag": "a9q2-QuestReceived", "skip_if": ["a9q2-QuestReceived", "a9q2-KilledBoss"]},
    {"verb": "goto", "area": "2_9_8", "skip_if": ["a9q2-HaveItem", "a9q2-DeliveredItem"]},
    {"verb": "kill", "target": "Garukhan/Garukhan", "area": "2_9_8", "appear_timeout": 15, "expect_flag": "a9q2-KilledBoss", "skip_if": "a9q2-KilledBoss"},
    {"verb": "talk", "npc": "SinGarukhan", "option": "Garukhan", "area": "2_9_8", "expect_flag": "SinGarukhanReward", "skip_if": "SinGarukhanReward", "optional": True},
    {"verb": "pantheon", "optional": True},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act9/SekhemaFeather", "area": "2_9_8", "expect_flag": "a9q2-HaveItem", "skip_if": ["a9q2-HaveItem", "a9q2-DeliveredItem"]},
    {"verb": "talk", "npc": "Irasha", "option": "Feather of Sekhema", "row": 1313, "area": "2_9_town", "expect_flag": "IrashaTakeFeather", "skip_if": ["a9q2-DeliveredItem", "IrashaTakeFeather"]},
    {"verb": "reward", "npc": "Irasha", "option": "Feather Reward", "area": "2_9_town", "skip_if": "a9q2-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act9/Book-a9q2", "expect_flag": "a9q2-UsedRewardBook"},
    {"verb": "passives"},
]
