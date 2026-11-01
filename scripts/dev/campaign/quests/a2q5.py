"""a2q5 "Through Sacred Ground": Yeena dreams of the Golden Hand (after a2q10's reward and a2q6's gem); it lies on the
altar in The Crypt Level 2. She takes it and gives a Book of Skill: one passive and two refund points once used."""
QUEST = "a2q5"
DONE = "a2q5-UsedRewardBook"

HAVE = "a2q5-HaveGoldenHand"
DELIVERED = "a2q5-DeliveredGoldenHand"
BOOK = "a2q5-HaveRewardBook"

STEPS = [
    {"verb": "talk", "npc": "Yeena", "option": "The Fellshrine Hand", "area": "1_2_town", "expect_flag": "a2q5-ReceivedQuest",
     "skip_if": ["a2q5-ReceivedQuest", HAVE, DELIVERED], "optional": True},
    {"verb": "goto", "area": "1_2_5_1", "skip_if": [HAVE, DELIVERED]},
    {"verb": "goto", "area": "1_2_5_2", "skip_if": [HAVE, DELIVERED]},
    {"verb": "interact", "target": "ChurchAltar", "skip_if": [HAVE, DELIVERED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/GoldenHand", "expect_flag": HAVE, "skip_if": [HAVE, DELIVERED]},
    {"verb": "talk", "npc": "Yeena", "option": "The Fellshrine Hand", "area": "1_2_town", "expect_flag": DELIVERED,
     "skip_if": DELIVERED},
    {"verb": "reward", "npc": "Yeena", "option": "Fellshrine Reward", "area": "1_2_town", "expect_flag": BOOK, "skip_if": [BOOK, DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/SkillBooks/Book-a2q5", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
