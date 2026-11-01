"""a7q9 "Queen of Despair" (act7.md): Sin's "Gruthkul"; she dies in The Dread Thicket, Sin harvests her (1132: the
Pantheon panel), Eramir's book (offer 53)."""
QUEST = "a7q9"
DONE = "a7q9-UsedRewardBook"
REQUIRES = ["a7q1"]

STEPS = [
    {"verb": "talk", "npc": "Act7/SinTown", "option": "Gruthkul", "area": "2_7_town", "expect_flag": "a7q9-QuestReceived", "skip_if": ["a7q9-QuestReceived", "a7q9-KilledBoss"]},
    {"verb": "goto", "area": "2_7_9", "skip_if": "a7q9-KilledBoss"},
    {"verb": "kill", "target": "DespairBear/DespairBear", "area": "2_7_9", "appear_timeout": 15, "expect_flag": "a7q9-KilledBoss", "skip_if": "a7q9-KilledBoss"},
    {"verb": "talk", "npc": "SinGruthkul", "option": "Gruthkul", "area": "2_7_9", "expect_flag": "SinGruthkulReward", "skip_if": "SinGruthkulReward", "optional": True},
    {"verb": "pantheon", "optional": True},
    {"verb": "talk", "npc": "Act7/Eramir", "option": "Gruthkul", "area": "2_7_town", "expect_flag": "EramirGruthkulReward", "skip_if": ["EramirGruthkulReward", "a7q9-HaveRewardBook"]},
    {"verb": "reward", "npc": "Act7/Eramir", "option": "Gruthkul Reward", "area": "2_7_town", "skip_if": "a7q9-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act7/Book-a7q9", "expect_flag": "a7q9-UsedRewardBook"},
    {"verb": "passives"},
]
