"""a1q9 "The Way Forward", Act 1's part: Tarkleigh's "Reaching the Forest" gives it; at Prisoner's Gate Piety has
blocked the road to The Western Forest with spikes (seeing the blockage: a1q9-SeenBlockage and a1q9-RoadBlocked, both
in Act1Complete). The road opens in Act 2 (the Thaumetic Emblem) and Bestel's reward comes after that: a2q11 finishes
the quest (its DONE is a1q9-UsedRewardBook), so this part is done once the blockage was seen."""
QUEST = "a1q9"
DONE = "a1q9-SeenBlockage"
REQUIRES = ["a1q2"]

STEPS = [
    {"verb": "talk", "npc": "Tarkleigh", "option": "Reaching the Forest", "area": "1_1_town", "expect_flag": "a1q9-QuestReceived",
     "skip_if": ["a1q9-QuestReceived", DONE, "Visited-2_1"], "optional": True},
    {"verb": "goto", "area": "1_1_8", "skip_if": DONE},
    {"verb": "tp", "target": "PietyBlockage", "skip_if": DONE},
    {"verb": "wait_flag", "flag": DONE, "timeout": 10, "skip_if": DONE},
]
