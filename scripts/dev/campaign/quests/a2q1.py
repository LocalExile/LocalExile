"""a2q1 "The Bandit Lord Kraityn", played the automation's way: the bandit choice is "kill all three" (a2q7), so
Kraityn at The Broken Bridge dies and drops his amulet, which a2q7 brings to Eramir. The help path (the bandit vote,
QuestStaticRewards 4: 8% movement speed) is not taken."""
QUEST = "a2q1"
DONE = "a2b-KilledDexBandit"

HAVE = "a2b-HaveDexAmulet"

STEPS = [
    {"verb": "goto", "area": "1_2_4", "skip_if": [DONE, "a2b-DeliveredAmulets"]},
    {"verb": "kill", "target": "BanditLeaderKraityn", "appear_timeout": 15, "expect_flag": DONE, "skip_if": DONE},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/BanditAmulets/DexAmulet", "expect_flag": HAVE,
     "skip_if": [HAVE, "a2b-DeliveredAmulets"]},
]
