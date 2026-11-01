"""a3q9 "Piety's Pets": Piety in The Lunaris Temple Level 2 (her arena, mapboss1 of pietyfight_2_1.arm); her death drops
the Tower Key (a3q10). Grigor's reward (offer 26) is a Book of Skill: one passive point once used."""
QUEST = "a3q9"
DONE = "a3q9-UsedRewardBook"
REQUIRES = ["a3q5"]

KILLED = "a3q9-KilledPiety"
KEY = ["a3q9-HaveTowerKey", "a3q9-UsedTowerKey"]
BOOK = "a3q9-HaveRewardBook"

STEPS = [
    {"verb": "talk", "npc": "Grigor", "option": "Piety", "area": "1_3_town", "expect_flag": "a3q9-QuestReceived",
     "skip_if": ["a3q9-QuestReceived", KILLED], "optional": True},
    {"verb": "goto", "area": "1_3_14_1", "skip_if": [KILLED, "Visited-3_14_2"]},
    {"verb": "goto", "area": "1_3_14_2", "skip_if": [*KEY]},
    {"verb": "kill", "target": "PietyBoss", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/TowerKey", "expect_flag": "a3q9-HaveTowerKey", "skip_if": KEY},
    {"verb": "talk", "npc": "Grigor", "option": "Piety", "area": "1_3_town", "expect_flag": "a3q9-Congratulated",
     "skip_if": ["a3q9-Congratulated", BOOK, DONE]},
    {"verb": "reward", "npc": "Grigor", "option": "Piety Reward", "area": "1_3_town", "expect_flag": BOOK, "skip_if": [BOOK, DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/SkillBooks/Book-a3q9", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
