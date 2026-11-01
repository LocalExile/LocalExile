"""a2q8 "Shadow of the Vaal": the Ancient Seal in The Vaal Ruins releases the darkness (the way north opens); with The
Apex (a2q7) the Apex door in The Caverns stands open; on top of The Ancient Pyramid the Dark Altar takes The Apex and
the Vaal Oversoul emerges. Its death (a2q8-KilledShadowMonster, FinishedVaalOversoulQuest) opens the exit to The City
of Sarn; the quest completes in The Sarn Encampment."""
QUEST = "a2q8"
DONE = "a2q8-KilledShadowMonster"
REQUIRES = ["a2q9"]

DARK = "a2q8-UnleashedDarkness"
PLACED = "a2q8-PlacedApex"

STEPS = [
    {"verb": "goto", "area": "1_2_11", "skip_if": DARK},
    {"verb": "interact", "target": "IncaDarknessRelease", "expect_flag": DARK, "skip_if": DARK},
    # The Apex, if a2q7 was left before its last step.
    {"verb": "reward", "npc": "Eramir", "option": "Take the Apex", "area": "1_2_town", "pick": ["The Apex"],
     "expect_flag": "a2q8-HaveApex", "skip_if": ["a2q8-HaveApex", PLACED, DONE]},
    {"verb": "talk", "npc": "Yeena", "option": "The Darkness", "area": "1_2_town", "expect_flag": "a2q8-QuestReceived",
     "skip_if": ["a2q8-QuestReceived", PLACED, DONE], "optional": True},
    {"verb": "talk", "npc": "Eramir", "option": "Yeena's Vision", "area": "1_2_town", "expect_flag": "a2q8-EramirClue",
     "skip_if": ["a2q8-EramirClue", PLACED, DONE], "optional": True},
    {"verb": "goto", "area": "1_2_14_2", "skip_if": [PLACED, DONE]},
    {"verb": "tp", "target": "ApexDoor", "skip_if": ["a2q8-SeenApexDoor", PLACED, DONE], "optional": True},
    {"verb": "goto", "area": "1_2_14_3", "skip_if": DONE},
    {"verb": "interact", "target": "IncaBossSpawner", "expect_flag": PLACED, "skip_if": [PLACED, DONE]},
    {"verb": "kill", "target": "BossIncaShadow", "appear_timeout": 20, "expect_flag": DONE, "skip_if": DONE},
    {"verb": "wait_flag", "flag": "FinishedVaalOversoulQuest", "timeout": 10},
    {"verb": "goto", "area": "1_3_1", "via": "transition", "skip_if": "Visited-3_1"},
    {"verb": "goto", "area": "1_3_town", "skip_if": "Visited-3_town"},
]
