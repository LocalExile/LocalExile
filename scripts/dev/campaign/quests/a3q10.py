"""a3q10 "The Sceptre of God": the Tower Key opens the Epic Door in The Imperial Gardens; atop The Upper Sceptre of God
Dominus dies into his second form, whose death sets a3q10-KilledDominus. Lady Dialla appears ("Dominus", "Highgate")
and the exit leads to Act 4's The Aqueduct; the quest completes in Highgate."""
QUEST = "a3q10"
DONE = "Visited-4_1"
REQUIRES = ["a3q9"]

USED = "a3q9-UsedTowerKey"
KILLED = "a3q10-KilledDominus"

STEPS = [
    {"verb": "goto", "area": "1_3_15", "skip_if": [USED, KILLED]},
    {"verb": "interact", "target": "EpicDoorLock", "expect_flag": USED, "skip_if": USED},
    {"verb": "goto", "area": "1_3_18_1", "skip_if": KILLED},
    {"verb": "goto", "area": "1_3_18_2", "skip_if": KILLED},
    {"verb": "kill", "target": "Pope/Pope", "appear_timeout": 20, "skip_if": KILLED},
    {"verb": "kill", "target": "Dominusdemon", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "talk", "npc": "DiallaTower", "option": "Dominus", "area": "1_3_18_2", "expect_flag": "a3q8-DiallaCongratulated",
     "skip_if": ["a3q8-DiallaCongratulated", "a4q1-QuestReceived"], "optional": True},
    {"verb": "talk", "npc": "DiallaTower", "option": "Highgate", "area": "1_3_18_2", "expect_flag": "a4q1-QuestReceived",
     "skip_if": "a4q1-QuestReceived", "optional": True},
    {"verb": "goto", "area": "1_4_1", "via": "transition", "skip_if": DONE},
    {"verb": "goto", "area": "1_4_town", "skip_if": "Visited-4_town", "optional": True},
]
