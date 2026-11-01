"""a2q8a3 "Shadow of the Vaal", Act 3's part: after the Vaal Oversoul the pyramid's exit leads to The City of Sarn; the
quest completes in The Sarn Encampment."""
QUEST = "a2q8a3"
DONE = "Visited-3_town"
REQUIRES = ["a2q8"]

STEPS = [
    {"verb": "goto", "area": "1_3_1", "skip_if": ["Visited-3_1", DONE]},
    {"verb": "goto", "area": "1_3_town", "skip_if": DONE},
]
