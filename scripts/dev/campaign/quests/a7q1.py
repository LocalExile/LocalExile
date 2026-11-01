"""a7q1 "The Master of a Million Faces" (act7.md): Eramir's "Greust" (the row of the Act 2 bandit choice), through
the Obsidian Key's passage and The Den into The Ashen Fields: Greust (Ralakesh's avatar when all three bandit lords
died) dies, Sin harvests Ralakesh (1130: the Pantheon panel), Eramir's book (offer 46)."""
QUEST = "a7q1"
DONE = "a7q1-UsedRewardBook"
REQUIRES = ["a7q3"]

STEPS = [
    {"verb": "talk", "npc": "Act7/Eramir", "option": "Greust", "area": "2_7_town", "expect_flag": "a7q1-QuestReceived", "skip_if": ["a7q1-QuestReceived", "a7q1-KilledBoss"]},
    {"verb": "goto", "area": "2_7_7", "skip_if": "a7q1-KilledBoss"},
    {"verb": "kill", "target": "Ralakesh/", "area": "2_7_7", "appear_timeout": 15, "expect_flag": "a7q1-KilledBoss", "skip_if": "a7q1-KilledBoss"},
    {"verb": "talk", "npc": "SinRalakesh", "option": "Ralakesh", "area": "2_7_7", "expect_flag": "SinRalakeshReward", "skip_if": "SinRalakeshReward", "optional": True},
    {"verb": "pantheon", "optional": True},
    {"verb": "talk", "npc": "Act7/Eramir", "option": "Ralakesh", "area": "2_7_town", "expect_flag": "a7q1-Congratulated", "skip_if": ["a7q1-Congratulated", "a7q1-HaveRewardBook"]},
    {"verb": "reward", "npc": "Act7/Eramir", "option": "Ralakesh Reward", "area": "2_7_town", "skip_if": "a7q1-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act7/Book-a7q1", "expect_flag": "a7q1-UsedRewardBook"},
    {"verb": "passives"},
]
