"""a5q3 "In Service to Science" (act5.md): Vilenta's Miasmeter lies in the Experimental Supplies of The Control Blocks;
her Book of Skill (offer 36; QuestStaticRewards 12: +1 passive)."""
QUEST = "a5q3"
DONE = "a5q3-UsedRewardBook"
REQUIRES = ["a5q1b"]

DELIVERED = "a5q3-DeliveredItem"

STEPS = [
    {"verb": "talk", "npc": "Vilenta", "option": "Miasmeter", "index": 14, "area": "1_5_town", "expect_flag": "a5q3-QuestReceived",
     "skip_if": ["a5q3-QuestReceived", DELIVERED]},
    {"verb": "goto", "area": "1_5_2", "skip_if": ["a5q3-HaveItem", DELIVERED]},
    {"verb": "tp", "target": "SupplyChest", "optional": True, "skip_if": ["a5q3-HaveItem", DELIVERED]},
    {"verb": "kill", "target": "near 30", "timeout": 20, "optional": True, "skip_if": ["a5q3-HaveItem", DELIVERED]},
    {"verb": "interact", "target": "SupplyChest", "area": "1_5_2", "skip_if": ["a5q3-HaveItem", DELIVERED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act5/Miasmeter", "expect_flag": "a5q3-HaveItem", "skip_if": ["a5q3-HaveItem", DELIVERED]},
    {"verb": "talk", "npc": "Vilenta", "option": "Miasmeter", "index": 15, "area": "1_5_town", "expect_flag": DELIVERED, "skip_if": DELIVERED},
    {"verb": "reward", "npc": "Vilenta", "option": "Miasmeter Reward", "area": "1_5_town", "skip_if": ["a5q3-HaveRewardBook", DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act5/Book-a5q3", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
