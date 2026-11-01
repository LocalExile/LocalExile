"""a3q2 "Lost in Love" (the Sewer Keys): Clarissa thanks the player for the bracelet and gives the Sewer Keys (offer 19);
used on the sewer grating in The Slums they open The Sewers (a3q11-UsedSewerKeys, the gate of the city's middle)."""
QUEST = "a3q2"
DONE = "a3q11-UsedSewerKeys"
REQUIRES = ["a3q1"]

STEPS = [
    {"verb": "talk", "npc": "ClarissaTown", "option": "Tolman", "area": "1_3_town", "expect_flag": "a3q2-Congratulated",
     "skip_if": ["a3q2-Congratulated", "a3q11-HaveSewerKeys", DONE], "optional": True},
    {"verb": "reward", "npc": "ClarissaTown", "option": "Take Sewer Keys", "area": "1_3_town", "pick": ["Sewer Keys"],
     "expect_flag": "a3q11-HaveSewerKeys", "skip_if": ["a3q11-HaveSewerKeys", DONE]},
    {"verb": "goto", "area": "1_3_2", "skip_if": DONE},
    {"verb": "interact", "target": "SewersLockedDoor", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "goto", "area": "1_3_10_1", "via": "transition", "skip_if": "Visited-3_10", "optional": True},
]
