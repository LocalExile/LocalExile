"""a1q8 "A Dirty Job": Tarkleigh wants The Fetid Pool cleared. Kadavrus the Defiler and every monster there must die
(a1q8-Cleared when the last one does); Tarkleigh's reward is the Book of Regrets, whose use gives two refund points."""
QUEST = "a1q8"
DONE = "a1q8-UsedRewardBook"
REQUIRES = ["a1q5"]

CLEARED = "a1q8-Cleared"
BOOK = "a1q8-HaveRewardBook"

STEPS = [
    {"verb": "talk", "npc": "Tarkleigh", "option": "The Fetid Pool", "area": "1_1_town", "expect_flag": "a1q8-QuestReceived",
     "skip_if": ["a1q8-QuestReceived", CLEARED], "optional": True},
    {"verb": "goto", "area": "1_1_3", "skip_if": CLEARED},
    {"verb": "goto", "area": "1_1_3a", "skip_if": CLEARED},
    {"verb": "kill", "target": "Kadavrus", "appear_timeout": 15, "expect_flag": "a1q8-KilledNecromancer",
     "skip_if": ["a1q8-KilledNecromancer", CLEARED]},
    # Every monster of the instance, the buried ones too: the last death sets the flag.
    {"verb": "command", "text": "/kill all", "skip_if": CLEARED},
    {"verb": "wait_flag", "flag": CLEARED, "timeout": 30, "skip_if": CLEARED},
    {"verb": "talk", "npc": "Tarkleigh", "option": "The Necromancer", "area": "1_1_town", "expect_flag": "a1q8-Congratulated",
     "skip_if": ["a1q8-Congratulated", BOOK, DONE]},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Necromancer Reward", "area": "1_1_town", "expect_flag": BOOK,
     "skip_if": [BOOK, DONE]},
    # The book is used from the inventory (C->S 0x58): its use_flag gives the refund points (QuestStaticRewards).
    {"verb": "use", "item": "Metadata/Items/QuestItems/SkillBooks/Book-a1q8", "expect_flag": DONE, "skip_if": DONE},
]
