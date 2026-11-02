"""a10q6 "Vilenta's Vengeance" (act10.md): Lani's quest; Vilenta dies in The Control Blocks; Lani's book (offer 69)."""
QUEST = "a10q6"
DONE = "a10q6-UsedRewardBook"
REQUIRES = ["a10q1"]

STEPS = [
    {"verb": "talk", "npc": "Act10/Lani", "option": "Vilenta", "row": 1410, "area": "2_10_town", "expect_flag": "a10q6-QuestReceived", "skip_if": ["a10q6-QuestReceived", "a10q6-KilledBoss"]},
    {"verb": "goto", "area": "2_10_7", "skip_if": "a10q6-KilledBoss"},
    {"verb": "kill", "target": "Vilenta/Vilenta", "area": "2_10_7", "appear_timeout": 15, "expect_flag": "a10q6-KilledBoss", "skip_if": "a10q6-KilledBoss"},
    {"verb": "talk", "npc": "Act10/Lani", "option": "Vilenta", "area": "2_10_town", "expect_flag": "LaniVilentaDead", "skip_if": ["LaniVilentaDead", "a10q6-HaveRewardBook"]},
    {"verb": "reward", "npc": "Act10/Lani", "option": "Vilenta Reward", "area": "2_10_town", "skip_if": "a10q6-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act10/Book-a10q6", "expect_flag": "a10q6-UsedRewardBook"},
    {"verb": "passives"},
]
