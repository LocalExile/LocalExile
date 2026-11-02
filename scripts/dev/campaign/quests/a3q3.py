"""a3q3 "The Gemling Queen": Grigor sends the player to the Gemling Queen's tomb; Lady Dialla in The Solaris Temple
Level 2 ("Introduction"). Usually done on a3q4's way already."""
QUEST = "a3q3"
DONE = "a3q3-DiallaIntroduced"
REQUIRES = ["a3q2"]

STEPS = [
    {"verb": "talk", "npc": "Grigor", "option": "The Gemling Queen", "area": "1_3_town", "skip_if": ["a3q3-QuestReceived", DONE],
     "optional": True},
    {"verb": "goto", "area": "1_3_8_2", "skip_if": DONE},
    {"verb": "talk", "npc": "Lady Dialla", "option": "Introduction", "area": "1_3_8_2", "expect_flag": DONE, "skip_if": DONE},
]
