"""a10q3a11 "An End to Hunger" in the Epilogue (act10.md): a10q3's reward steps in Karui Shores' log; Lani there
(1497/1498) gives the book when it was not taken in Oriath Docks."""
QUEST = "a10q3a11"
DONE = "a10q3-UsedRewardBook"
REQUIRES = ["A11q0"]

STEPS = [
    {"verb": "goto", "area": "2_11_endgame_town"},
    {"verb": "talk", "npc": "Epilogue/Lani", "option": "Kitava", "area": "2_11_endgame_town", "expect_flag": "LaniKitavaReward", "skip_if": ["LaniKitavaReward", "a10q3-HaveRewardBook"]},
    {"verb": "reward", "npc": "Epilogue/Lani", "option": "Kitava Reward", "area": "2_11_endgame_town", "skip_if": "a10q3-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act10/Book-a10q3", "expect_flag": "a10q3-UsedRewardBook"},
    {"verb": "passives"},
]
