"""a6q5 "Bestel's Epic" (act6.md): the storage chest on The Tidal Island's wreck holds Bestel's manuscript; his reward
(offer 43, belts and amulets)."""
QUEST = "a6q5"
DONE = "a6q5-QuestRewardTaken"
REQUIRES = ["a5q6"]

DELIVERED = "a6q5-DeliveredItem"

STEPS = [
    {"verb": "talk", "npc": "Act6/Bestel", "option": "Bestel's Epic", "index": 15, "area": "2_6_town", "expect_flag": "a6q5-QuestReceived",
     "skip_if": ["a6q5-QuestReceived", DELIVERED]},
    {"verb": "goto", "area": "2_6_3", "skip_if": ["a6q5-HaveItem", DELIVERED]},
    {"verb": "tp", "target": "BestelChest", "optional": True, "skip_if": ["a6q5-HaveItem", DELIVERED]},
    {"verb": "kill", "target": "near 30", "timeout": 20, "optional": True, "skip_if": ["a6q5-HaveItem", DELIVERED]},
    {"verb": "interact", "target": "BestelChest", "area": "2_6_3", "skip_if": ["a6q5-HaveItem", DELIVERED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act6/BestelsManuscript", "expect_flag": "a6q5-HaveItem", "skip_if": ["a6q5-HaveItem", DELIVERED]},
    {"verb": "talk", "npc": "Act6/Bestel", "option": "Bestel's Epic", "index": 16, "area": "2_6_town", "expect_flag": DELIVERED, "skip_if": DELIVERED},
    {"verb": "reward", "npc": "Act6/Bestel", "option": "Bestel's Epic Reward", "area": "2_6_town", "skip_if": DONE},
]
