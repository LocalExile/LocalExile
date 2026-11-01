"""a2q3 "The Bandit Lord Oak", played the automation's way: the bandit choice is "kill all three" (a2q7), so Oak in
The Wetlands dies and drops his amulet, which a2q7 brings to Eramir. The help path (the bandit vote,
QuestStaticRewards 6: +40 to maximum life) is not taken."""
QUEST = "a2q3"
DONE = "a2b-KilledStrBandit"

HAVE = "a2b-HaveStrAmulet"

STEPS = [
    {"verb": "goto", "area": "1_2_12", "skip_if": [DONE, "a2b-DeliveredAmulets"]},
    {"verb": "kill", "target": "BanditLeaderOak", "appear_timeout": 15, "expect_flag": DONE, "skip_if": DONE},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/BanditAmulets/StrAmulet", "expect_flag": HAVE,
     "skip_if": [HAVE, "a2b-DeliveredAmulets"]},
]
