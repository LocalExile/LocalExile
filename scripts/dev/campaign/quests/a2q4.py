"""a2q4 "Sharp and Cruel": Helena asks for Maligaro's Spike, Silk knows where it is: the Weaver in The Weaver's
Chambers drops it. Silk looks at it (the spike stays with the player: a2q9 needs it) and rewards (offer 15)."""
QUEST = "a2q4"
DONE = "a2q4-QuestRewardTaken"

HAVE = "a2q4-HavePoisonSpear"
DELIVERED = "a2q4-DeliveredSpear"

STEPS = [
    {"verb": "talk", "npc": "HelenaTown", "option": "Maligaro's Spike", "area": "1_2_town", "expect_flag": "a2q4-ReceivedQuest",
     "skip_if": ["a2q4-ReceivedQuest", HAVE, DELIVERED], "optional": True},
    {"verb": "talk", "npc": "Silk", "option": "Maligaro's Spike", "area": "1_2_town", "expect_flag": "a2q4-SilkClue",
     "skip_if": ["a2q4-SilkClue", HAVE, DELIVERED], "optional": True},
    {"verb": "goto", "area": "1_2_10", "skip_if": [HAVE, DELIVERED]},
    {"verb": "kill", "target": "SpiderBossWeaver", "appear_timeout": 15, "skip_if": [HAVE, DELIVERED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/PoisonSpear", "expect_flag": HAVE, "skip_if": [HAVE, DELIVERED]},
    {"verb": "talk", "npc": "Silk", "option": "Maligaro's Spike", "area": "1_2_town", "expect_flag": DELIVERED,
     "skip_if": [DELIVERED, DONE]},
    {"verb": "reward", "npc": "Silk", "option": "Maligaro's Spike Reward", "area": "1_2_town", "skip_if": DONE},
    {"verb": "passives"},
]
