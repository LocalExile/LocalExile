"""a2q2 "The Bandit Lord Alira", played the automation's way: the bandit choice is "kill all three" (a2q7), so Alira
in The Western Forest dies and drops her amulet, which a2q7 brings to Eramir. The help path (the bandit vote,
QuestStaticRewards 5: +15% to all elemental resistances) is not taken."""
QUEST = "a2q2"
DONE = "a2b-KilledIntBandit"

HAVE = "a2b-HaveIntAmulet"

STEPS = [
    {"verb": "goto", "area": "1_2_9", "skip_if": [DONE, "a2b-DeliveredAmulets"]},
    {"verb": "kill", "target": "BanditLeaderAlira", "appear_timeout": 15, "expect_flag": DONE, "skip_if": DONE},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/BanditAmulets/IntAmulet", "expect_flag": HAVE,
     "skip_if": [HAVE, "a2b-DeliveredAmulets"]},
]
