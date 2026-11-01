"""a8q4 "Reflection of Terror" (act8.md): Sin's row (after the Wings, 1195); Yugul dies in The High Gardens, Sin
harvests him (1226: the Pantheon panel), Hargan's book (offer 55)."""
QUEST = "a8q4"
DONE = "a8q4-UsedRewardBook"
REQUIRES = ["a8q5"]

STEPS = [
    {"verb": "talk", "npc": "Act8/SinTown", "option": "Reflection of Terror", "area": "2_8_town", "expect_flag": "a8q4-QuestReceived", "skip_if": ["a8q4-QuestReceived", "a8q4-KilledBoss"]},
    {"verb": "goto", "area": "2_8_4", "skip_if": "a8q4-KilledBoss"},
    {"verb": "kill", "target": "SilverFrog", "area": "2_8_4", "appear_timeout": 15, "expect_flag": "a8q4-KilledBoss", "skip_if": "a8q4-KilledBoss"},
    {"verb": "talk", "npc": "SinYugul", "option": "Yugul", "area": "2_8_4", "expect_flag": "SinYugulReward", "skip_if": "SinYugulReward", "optional": True},
    {"verb": "pantheon", "optional": True},
    {"verb": "talk", "npc": "Act8/Hargan", "option": "Yugul", "area": "2_8_town", "expect_flag": "HarganYugulReward", "skip_if": ["HarganYugulReward", "a8q4-HaveRewardBook"]},
    {"verb": "reward", "npc": "Act8/Hargan", "option": "Yugul Reward", "area": "2_8_town", "skip_if": "a8q4-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act8/Book-a8q4", "expect_flag": "a8q4-UsedRewardBook"},
    {"verb": "passives"},
]
