"""a8q1 "Essence of the Hag" (act8.md): Sin's and Hargan's talks; Doedre the Vile dies in Doedre's Cesspool (the
gate of the rest of Act 8), Sin (1224), Hargan's reward (offer 54: rings, gloves)."""
QUEST = "a8q1"
DONE = "a8q1-QuestRewardTaken"
REQUIRES = ["a7q4"]

STEPS = [
    {"verb": "goto", "area": "2_8_town"},
    {"verb": "talk", "npc": "Act8/SinTown", "option": "Doedre Darktongue", "area": "2_8_town", "expect_flag": "a8q1-QuestReceived", "skip_if": ["a8q1-QuestReceived", "a8q1-KilledBoss"]},
    {"verb": "talk", "npc": "Act8/Hargan", "option": "Doedre Darktongue", "area": "2_8_town", "skip_if": ["HarganDoedreQuest", "a8q1-KilledBoss"], "optional": True},
    {"verb": "goto", "area": "2_8_2_2", "skip_if": "a8q1-KilledBoss"},
    {"verb": "kill", "target": "DoedreSewers", "area": "2_8_2_2", "appear_timeout": 15, "expect_flag": "a8q1-KilledBoss", "skip_if": "a8q1-KilledBoss"},
    {"verb": "talk", "npc": "SinDoedre", "option": "Doedre", "area": "2_8_2_2", "skip_if": "SinDoedreReward", "optional": True},
    {"verb": "talk", "npc": "Act8/Hargan", "option": "Doedre Darktongue", "area": "2_8_town", "expect_flag": "HarganDoedreReward", "skip_if": "HarganDoedreReward"},
    {"verb": "reward", "npc": "Act8/Hargan", "option": "Doedre Reward", "area": "2_8_town", "pick": ["Two-Stone Ring", "Unset Ring", "Ring"]},
]
