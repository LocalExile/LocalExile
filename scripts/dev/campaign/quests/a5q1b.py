"""a5q1b "Return to Oriath" (act5.md): Overseer Krow guards The Slave Pens' way into Overseer's Tower; Lani's reward
(offer 34, magic and rare rings)."""
QUEST = "a5q1b"
DONE = "a5q1-QuestRewardTaken"

STEPS = [
    {"verb": "goto", "area": "1_5_1", "skip_if": "a5q1-KilledBoss"},
    {"verb": "kill", "target": "TemplarSlaveDriverBoss", "appear_timeout": 15, "expect_flag": "a5q1-KilledBoss", "skip_if": "a5q1-KilledBoss"},
    {"verb": "goto", "area": "1_5_town"},
    {"verb": "talk", "npc": "Lani", "option": "Overseer Krow", "area": "1_5_town", "expect_flag": "a5q1-Congratulated",
     "skip_if": ["a5q1-Congratulated", DONE]},
    {"verb": "reward", "npc": "Lani", "option": "Overseer Reward", "area": "1_5_town", "pick": ["Two-Stone Ring", "Ring"], "skip_if": DONE},
]
