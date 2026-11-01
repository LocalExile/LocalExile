"""a5q4 "Death to Purity" (act5.md): the Eyes of Zeal open the Templar Courts door in Oriath Square; High Templar Avarius
becomes Innocence in The Chamber of Innocence; Sin appears where Innocence falls and starts a5q6 (882 → 883 → 884,
talk pages); Lani's reward (offer 37: the Uncarved Gemstone)."""
QUEST = "a5q4"
DONE = "a5q4-QuestRewardTaken"
REQUIRES = ["a5q2"]

KILLED = "a5q4-KilledBoss"

STEPS = [
    {"verb": "goto", "area": "1_5_3", "skip_if": ["a5q2-UsedKey", KILLED]},
    {"verb": "interact", "target": "TemplarCourtsDoor", "area": "1_5_3", "expect_flag": "a5q2-UsedKey", "skip_if": ["a5q2-UsedKey", KILLED]},
    {"verb": "goto", "area": "1_5_4", "skip_if": KILLED},
    {"verb": "goto", "area": "1_5_5", "skip_if": KILLED},
    {"verb": "kill", "target": "High Templar Avarius", "appear_timeout": 15, "skip_if": KILLED},
    {"verb": "kill", "target": "AvariusCasticusDivine", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "talk", "npc": "SinInnocence", "option": "Kitava", "area": "1_5_5", "expect_flag": "a5q6-QuestReceived",
     "skip_if": "a5q6-QuestReceived"},
    {"verb": "talk", "npc": "Lani", "option": "High Templar Avarius", "area": "1_5_town", "expect_flag": "a5q4-Congratulated",
     "skip_if": ["a5q4-Congratulated", DONE]},
    {"verb": "reward", "npc": "Lani", "option": "Avarius Reward", "area": "1_5_town", "pick": ["Uncarved Gemstone"], "skip_if": DONE},
]
