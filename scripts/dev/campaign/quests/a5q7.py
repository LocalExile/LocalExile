"""a5q7 "Kitava's Torments" (act5.md): the three Karui Relic Cases of The Reliquary; Lani takes the Torments and gives a
Book of Skill (offer 39; QuestStaticRewards 13: +1 passive, +2 respec)."""
QUEST = "a5q7"
DONE = "a5q7-UsedRewardBook"
REQUIRES = ["a5q4"]

DELIVERED = "a5q7-DeliveredItems"

STEPS = [
    {"verb": "talk", "npc": "Lani", "option": "Kitava's Torments", "index": 16, "area": "1_5_town", "expect_flag": "a5q7-QuestReceived",
     "skip_if": ["a5q7-QuestReceived", DELIVERED]},
    {"verb": "goto", "area": "1_5_7", "skip_if": DELIVERED},
]
for n in (1, 2, 3):
    STEPS += [
        {"verb": "tp", "target": f"RelicCase{n}", "area": "1_5_7", "skip_if": [f"a5q7-HaveItem{n}", DELIVERED]},
        # The cases stand among Desperate Souls, whose hits break off the opening: clear them first.
        {"verb": "kill", "target": "near 30", "timeout": 20, "optional": True, "skip_if": [f"a5q7-HaveItem{n}", DELIVERED]},
        {"verb": "interact", "target": f"RelicCase{n}", "area": "1_5_7", "skip_if": [f"a5q7-HaveItem{n}", DELIVERED]},
        {"verb": "pickup", "item": f"Metadata/Items/QuestItems/Act5/Torment{n}", "expect_flag": f"a5q7-HaveItem{n}",
         "skip_if": [f"a5q7-HaveItem{n}", DELIVERED]},
    ]
STEPS += [
    {"verb": "talk", "npc": "Lani", "option": "Kitava's Torments", "index": 17, "area": "1_5_town", "expect_flag": DELIVERED, "skip_if": DELIVERED},
    {"verb": "reward", "npc": "Lani", "option": "Torments Reward", "area": "1_5_town", "skip_if": ["a5q7-HaveRewardBook", DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act5/Book-a5q7", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
