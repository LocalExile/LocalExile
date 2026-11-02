"""a6q2 "Essence of Umbra" (act6.md): Sin's task (town); Reassembled Brutus and Shavronne the Returned in her tower,
whose death opens Prisoner's Gate and the forests; Sin takes her soul (995); Tarkleigh's reward (offer 40, helmets)."""
QUEST = "a6q2"
DONE = "a6q2-QuestRewardTaken"
REQUIRES = ["a6q3"]

KILLED = "a6q2-KilledBoss"

STEPS = [
    {"verb": "talk", "npc": "Act6/SinTown", "option": "Shavronne of Umbra", "area": "2_6_town", "expect_flag": "a6q2-QuestReceived",
     "skip_if": ["a6q2-QuestReceived", KILLED], "optional": True},
    {"verb": "goto", "area": "2_6_7_2", "skip_if": KILLED},
    {"verb": "kill", "target": "BruteTower", "appear_timeout": 15, "skip_if": KILLED, "optional": True},
    {"verb": "kill", "target": "ShavronneTower", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "talk", "npc": "SinShavronne", "option": "Shavronne", "area": "2_6_7_2", "expect_flag": "SinShavronneReward",
     "skip_if": "SinShavronneReward", "optional": True},
    {"verb": "talk", "npc": "Act6/Tarkleigh", "option": "Shavronne of Umbra", "area": "2_6_town", "expect_flag": "TarkleighOnShavronneDead",
     "skip_if": ["TarkleighOnShavronneDead", DONE]},
    {"verb": "reward", "npc": "Act6/Tarkleigh", "option": "Shavronne Reward", "area": "2_6_town", "skip_if": DONE},
]
