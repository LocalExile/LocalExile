"""a8q6 "Love is Dead" (act8.md): Clarissa's "Reviving Tolman"; the Sealed Casket on The Quay holds the Ankh;
Clarissa at the resurrection site takes it (1153: in 3.29 the row also ends the fight, a8q6-KilledBoss), her Tolman
talk, then Clarissa's book in town (offer 57: 1 passive, 2 respec)."""
QUEST = "a8q6"
DONE = "a8q6-UsedRewardBook"
REQUIRES = ["a8q1"]

STEPS = [
    {"verb": "talk", "npc": "ClarissaTown", "option": "Reviving Tolman", "area": "2_8_town", "expect_flag": "a8q6-QuestReceived", "skip_if": ["a8q6-QuestReceived", "a8q6-KilledBoss"]},
    {"verb": "goto", "area": "2_8_8", "skip_if": ["a8q6-KilledBoss", "a8q6-HaveItem"]},
    {"verb": "interact", "target": "AnkhChest", "area": "2_8_8", "skip_if": ["a8q6-HaveItem", "a8q6-KilledBoss"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act8/AnkhOfEternity", "expect_flag": "a8q6-HaveItem", "skip_if": ["a8q6-HaveItem", "a8q6-KilledBoss"]},
    {"verb": "talk", "npc": "ClarissaQuay", "option": "The Ankh of Eternity", "area": "2_8_8", "expect_flag": "a8q6-KilledBoss", "skip_if": "a8q6-KilledBoss"},
    {"verb": "talk", "npc": "ClarissaQuay", "option": "Tolman", "area": "2_8_8", "skip_if": ["ClarissaOnTolmanZombieDead", "a8q6-Congratulated"], "optional": True},
    {"verb": "talk", "npc": "ClarissaTown", "option": "Tolman", "area": "2_8_town", "expect_flag": "a8q6-Congratulated", "skip_if": ["a8q6-Congratulated", "a8q6-HaveRewardBook"]},
    {"verb": "reward", "npc": "ClarissaTown", "option": "Tolman Reward", "area": "2_8_town", "skip_if": "a8q6-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act8/Book-a8q6", "expect_flag": "a8q6-UsedRewardBook"},
    {"verb": "passives"},
]
