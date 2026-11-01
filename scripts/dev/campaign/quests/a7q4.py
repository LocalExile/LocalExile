"""a7q4 "The Mother of Spiders" (act7.md), Act 7 -> 8: Yeena's "Silk"; Arakaali dies in The Temple of Decay Level 2,
Sin harvests her (1135: the Pantheon panel), her bridge lowers and the exit into The Sarn Ramparts opens."""
QUEST = "a7q4"
DONE = "Visited-8_1"
REQUIRES = ["a7q7"]

STEPS = [
    {"verb": "talk", "npc": "YeenaTown", "option": "Silk", "area": "2_7_town", "skip_if": ["a7q4-QuestReceived", "a7q4-KilledBoss"], "optional": True},
    {"verb": "goto", "area": "2_7_12_2", "skip_if": "a7q4-KilledBoss"},
    {"verb": "kill", "target": "VaalSpiderGod/Arakaali", "area": "2_7_12_2", "appear_timeout": 15, "expect_flag": "a7q4-KilledBoss", "skip_if": "a7q4-KilledBoss"},
    {"verb": "talk", "npc": "SinArakaali", "option": "Arakaali", "area": "2_7_12_2", "expect_flag": "SinArakaaliReward", "skip_if": "SinArakaaliReward", "optional": True},
    {"verb": "pantheon", "optional": True},
    {"verb": "goto", "area": "2_8_1", "via": "transition"},
    {"verb": "wait_flag", "flag": "Visited-8_1", "timeout": 20},
]
