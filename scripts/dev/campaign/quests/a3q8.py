"""a3q8 "Sever the Right Hand": Maramoa wants General Gravicius dead (The Ebony Barracks, beyond the burnt blockage).
Her reward is offer 23 (skill gems)."""
QUEST = "a3q8"
DONE = "a3q8-QuestRewardTaken"
REQUIRES = ["a3q5"]

KILLED = "a3q8-KilledGeneral"

STEPS = [
    {"verb": "talk", "npc": "Maramoa", "option": "General Gravicius", "area": "1_3_town", "expect_flag": "a3q8-QuestReceived",
     "skip_if": ["a3q8-QuestReceived", KILLED], "optional": True},
    {"verb": "goto", "area": "1_3_13", "skip_if": KILLED},
    {"verb": "kill", "target": "AxisGeneral", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "talk", "npc": "Maramoa", "option": "General Gravicius", "area": "1_3_town", "expect_flag": "a3q8-Congratulated",
     "skip_if": ["a3q8-Congratulated", DONE]},
    {"verb": "reward", "npc": "Maramoa", "option": "Gravicius Reward", "area": "1_3_town", "skip_if": DONE},
    {"verb": "passives"},
]
