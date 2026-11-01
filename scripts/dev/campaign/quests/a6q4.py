"""a6q4 "Fallen from Grace" (act6.md): Lilly's task, clear every monster of The Twilight Strand, her Book of Regrets
(offer 42; QuestStaticRewards 16: +2 respec)."""
QUEST = "a6q4"
DONE = "a6q4-UsedRewardBook"
REQUIRES = ["a5q6"]

CLEARED = "a6q4-AreaCleared"

STEPS = [
    {"verb": "talk", "npc": "Act6/Lilly", "option": "Introduction", "area": "2_6_town", "skip_if": ["LillyIntroduction", CLEARED], "optional": True},
    {"verb": "talk", "npc": "Act6/Lilly", "option": "The Twilight Strand", "index": 5, "area": "2_6_town", "expect_flag": "a6q4-QuestReceived",
     "skip_if": ["a6q4-QuestReceived", CLEARED]},
    {"verb": "goto", "area": "2_6_1", "skip_if": CLEARED},
    {"verb": "kill", "target": "all", "area": "2_6_1", "timeout": 60, "expect_flag": CLEARED, "skip_if": CLEARED},
    {"verb": "talk", "npc": "Act6/Lilly", "option": "The Twilight Strand", "index": 6, "area": "2_6_town", "expect_flag": "a6q4-Congratulated",
     "skip_if": ["a6q4-Congratulated", "a6q4-HaveRewardBook", DONE]},
    {"verb": "reward", "npc": "Act6/Lilly", "option": "Twilight Strand Reward", "area": "2_6_town", "skip_if": ["a6q4-HaveRewardBook", DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act6/Book-a6q4", "expect_flag": DONE, "skip_if": DONE},
]
