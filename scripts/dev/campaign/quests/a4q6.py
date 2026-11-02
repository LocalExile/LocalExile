"""a4q6 "An Indomitable Spirit" (act4.md): Tasuni's task; Deshret's spirit in The Mines Level 2 is freed by using it;
Tasuni's Book of Skill (offer 31; QuestStaticRewards 11: +1 passive, +2 respec)."""
QUEST = "a4q6"
DONE = "a4q6-UsedRewardBook"
REQUIRES = ["a4q2"]

FREED = "a4q6-DeshretFreed"

STEPS = [
    {"verb": "talk", "npc": "Tasuni", "option": "Deshret", "index": 18, "area": "1_4_town", "expect_flag": "a4q6-QuestReceived",
     "skip_if": ["a4q6-QuestReceived", FREED]},
    {"verb": "goto", "area": "1_4_3_2", "skip_if": FREED},
    {"verb": "tp", "target": "DeshretSpirit", "skip_if": ["a4q6-DeshretSpiritSeen", FREED], "optional": True},
    {"verb": "tp", "target": "DeshretSpirit", "optional": True, "skip_if": FREED},
    {"verb": "kill", "target": "near 30", "timeout": 20, "optional": True, "skip_if": FREED},
    {"verb": "interact", "target": "DeshretSpirit", "area": "1_4_3_2", "expect_flag": FREED, "skip_if": FREED},
    {"verb": "talk", "npc": "Tasuni", "option": "Deshret", "index": 17, "area": "1_4_town", "expect_flag": "a4q6-Congratulated",
     "skip_if": ["a4q6-Congratulated", "a4q6-HaveRewardBook", DONE]},
    {"verb": "reward", "npc": "Tasuni", "option": "Deshret Reward", "area": "1_4_town", "skip_if": ["a4q6-HaveRewardBook", DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act4/Book-a4q6", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
