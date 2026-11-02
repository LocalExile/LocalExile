"""a3q5 "Fiery Dust": the Undying blockage bars The Sewers' way west. Lady Dialla wants Thaumetic Sulphite (the Supply
Container in The Docks) and gives Infernal Talc (offer 22), which burns the blockage (a3q5-UsedTalc: the Ebony Barracks
and the west of Sarn open)."""
QUEST = "a3q5"
DONE = "a3q5-UsedTalc"
REQUIRES = ["a3q4"]

HAVE = "a3q5-HaveSulphite"
DELIVERED = "a3q5-DeliveredSulphite"
TALC = "a3q5-HaveTalc"

STEPS = [
    {"verb": "talk", "npc": "Lady Dialla", "option": "The Sewers", "area": "1_3_8_2", "expect_flag": "a3q5-DiallaClue",
     "skip_if": ["a3q5-DiallaClue", DELIVERED, TALC, DONE], "optional": True},
    {"verb": "talk", "npc": "Lady Dialla", "option": "Thaumetic Sulphite", "area": "1_3_8_2", "expect_flag": "a3q5-QuestReceived",
     "skip_if": ["a3q5-QuestReceived", HAVE, DELIVERED, TALC, DONE], "optional": True},
    {"verb": "goto", "area": "1_3_9", "skip_if": [HAVE, DELIVERED, TALC, DONE]},
    {"verb": "interact", "target": "SulphiteChest", "skip_if": [HAVE, DELIVERED, TALC, DONE]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/SulphiteFlask", "expect_flag": HAVE, "skip_if": [HAVE, DELIVERED, TALC, DONE]},
    {"verb": "talk", "npc": "Lady Dialla", "option": "Thaumetic Sulphite", "area": "1_3_8_2", "expect_flag": DELIVERED,
     "skip_if": [DELIVERED, TALC, DONE]},
    {"verb": "talk", "npc": "Lady Dialla", "option": "Infernal Talc", "area": "1_3_8_2", "skip_if": [TALC, DONE], "optional": True},
    {"verb": "reward", "npc": "Lady Dialla", "option": "Take Infernal Talc", "area": "1_3_8_2", "pick": ["Infernal Talc"],
     "expect_flag": TALC, "skip_if": [TALC, DONE]},
    {"verb": "goto", "area": "1_3_10_1", "skip_if": DONE},
    {"verb": "interact", "target": "BioWall", "expect_flag": DONE, "skip_if": DONE},
]
