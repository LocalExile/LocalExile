"""a1q3 "The Siren's Cadence": Nessa's "Merveil" (after The Ship Graveyard) gives it and her help once The Cavern of
Wrath is found ("Merveil's Lair", then "Merveil Reward"). Merveil, the Siren becomes Merveil, the Twisted in The
Cavern of Anger; the second form's death (a1q3-KilledSeaWitch) unblocks the exit into Act 2."""
QUEST = "a1q3"
DONE = "a1q3-QuestRewardTaken"
REQUIRES = ["a1q2"]

KILLED = "a1q3-KilledSeaWitch"

STEPS = [
    {"verb": "talk", "npc": "Nessa", "option": "Merveil", "area": "1_1_town", "expect_flag": "a1q3-ReceivedQuest",
     "skip_if": ["a1q3-ReceivedQuest", KILLED], "optional": True},
    {"verb": "goto", "area": "1_1_9", "skip_if": ["Visited-1_11", KILLED]},
    {"verb": "goto", "area": "1_1_11_1", "skip_if": ["Visited-1_11", KILLED]},
    {"verb": "talk", "npc": "Nessa", "option": "Merveil's Lair", "area": "1_1_town", "expect_flag": "a1q3-NessaCongratulated",
     "skip_if": ["a1q3-NessaCongratulated", "a1q3-NessaHelped", DONE, KILLED], "optional": True},
    {"verb": "reward", "npc": "Nessa", "option": "Merveil Reward", "area": "1_1_town", "skip_if": DONE, "optional": True},
    {"verb": "goto", "area": "1_1_11_1", "skip_if": KILLED},
    {"verb": "goto", "area": "1_1_11_2", "skip_if": KILLED},
    {"verb": "kill", "target": "Merveil, the Siren", "appear_timeout": 15, "skip_if": KILLED},
    {"verb": "kill", "target": "Merveil, the Twisted", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    # After the kill (or when the help was not taken before) Nessa's "Merveil" gossip and the reward.
    {"verb": "talk", "npc": "Nessa", "option": "Merveil", "area": "1_1_town", "skip_if": ["a1q3-NessaHelped", DONE], "optional": True},
    {"verb": "reward", "npc": "Nessa", "option": "Merveil Reward", "area": "1_1_town", "skip_if": DONE},
    {"verb": "goto", "area": "1_1_11_2", "via": "waypoint"},
    {"verb": "goto", "area": "1_2_1", "via": "transition"},
    {"verb": "passives"},
]
