"""a8q7 "The Gemling Legion" (act8.md): Maramoa's quest; the seven of The Grain Gate die (a8q7-KilledBoss once none
is left), Maramoa's book (offer 58)."""
QUEST = "a8q7"
DONE = "a8q7-UsedRewardBook"
REQUIRES = ["a8q6"]

STEPS = [
    {"verb": "talk", "npc": "Act8/Maramoa", "option": "The Gemling Legion", "area": "2_8_town", "expect_flag": "a8q7-QuestReceived", "skip_if": ["a8q7-QuestReceived", "a8q7-KilledBoss"]},
    {"verb": "goto", "area": "2_8_9", "skip_if": "a8q7-KilledBoss"},
    {"verb": "kill", "target": "GemlingLegionnaire", "area": "2_8_9", "appear_timeout": 15, "expect_flag": "a8q7-KilledBoss", "skip_if": "a8q7-KilledBoss"},
    {"verb": "talk", "npc": "Act8/Maramoa", "option": "The Gemling Legion", "area": "2_8_town", "expect_flag": "a8q7-Congratulated", "skip_if": ["a8q7-Congratulated", "a8q7-HaveRewardBook"]},
    {"verb": "reward", "npc": "Act8/Maramoa", "option": "Gemling Legion Reward", "area": "2_8_town", "skip_if": "a8q7-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act8/Book-a8q7", "expect_flag": "a8q7-UsedRewardBook"},
    {"verb": "passives"},
]
