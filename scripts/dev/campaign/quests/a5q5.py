"""a5q5 "The King's Feast" (act5.md): Bannon waits in The Chamber of Innocence after Avarius ("Rescue"), then in
Overseer's Tower; Utula, Stone and Steel in The Ruined Square; Bannon's reward (offer 38)."""
QUEST = "a5q5"
DONE = "a5q5-QuestRewardTaken"
REQUIRES = ["a5q4"]

STEPS = [
    {"verb": "talk", "npc": "BannonInnocence", "option": "Rescue", "area": "1_5_5", "expect_flag": "BannonRescued", "skip_if": "BannonRescued"},
    {"verb": "talk", "npc": "BannonTown", "option": "Introduction", "area": "1_5_town", "skip_if": ["BannonIntroduction", "a5q5-KilledBoss"],
     "optional": True},
    {"verb": "talk", "npc": "BannonTown", "option": "Utula", "index": 7, "area": "1_5_town", "expect_flag": "a5q5-QuestReceived",
     "skip_if": ["a5q5-QuestReceived", "a5q5-KilledBoss"]},
    {"verb": "goto", "area": "1_5_3b", "skip_if": "a5q5-KilledBoss"},
    {"verb": "kill", "target": "BlacksmithGolem", "appear_timeout": 15, "expect_flag": "a5q5-KilledBoss", "skip_if": "a5q5-KilledBoss"},
    {"verb": "talk", "npc": "BannonTown", "option": "Utula", "index": 8, "area": "1_5_town", "expect_flag": "a5q5-Congratulated",
     "skip_if": ["a5q5-Congratulated", DONE]},
    {"verb": "reward", "npc": "BannonTown", "option": "Utula Reward", "area": "1_5_town", "skip_if": DONE},
]
