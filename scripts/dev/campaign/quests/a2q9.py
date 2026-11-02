"""a2q9 "The Root of the Problem": the tree in The Wetlands blocks the way into The Vaal Ruins. Used with Maligaro's
Spike and the Baleful Gem it is poisoned (both items go) and the way opens."""
QUEST = "a2q9"
DONE = "a2q9-PoisonedTree"
REQUIRES = ["a2q6", "a2q4"]

STEPS = [
    {"verb": "talk", "npc": "HelenaTown", "option": "Lorrata", "area": "1_2_town", "skip_if": ["PoisonTreeClue", DONE], "optional": True},
    {"verb": "goto", "area": "1_2_12", "skip_if": DONE},
    {"verb": "interact", "target": "PoisonTree", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "goto", "area": "1_2_11", "via": "transition", "skip_if": "Visited-2_11", "optional": True},
]
