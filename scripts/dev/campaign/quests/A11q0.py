"""A11q0 "Toward the Future" (act10.md): Lilly's "Set Sail from Oriath" (1433: SetQuestFlag A10LillySailedAway,
AreaTransitionTo 2_11_endgame_town) moves the player to Karui Shores, the Epilogue."""
QUEST = "A11q0"
DONE = "A10LillySailedAway"
REQUIRES = ["a10q3"]

STEPS = [
    {"verb": "talk", "npc": "Act10/Lilly", "option": "Set Sail from Oriath", "area": "2_10_town", "no_wait": True, "skip_if": "A10LillySailedAway"},
    {"verb": "wait_flag", "flag": "A10LillySailedAway", "timeout": 30},
    {"verb": "goto", "area": "2_11_endgame_town"},
]
