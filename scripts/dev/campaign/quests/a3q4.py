"""a3q4 "The Ribbon Spool": the Blackguard Chest on The Battlefront's bridge holds Lady Dialla's Ribbon Spool; she is in
The Solaris Temple Level 2 (a3q3 meets her there). Her reward (offer 21) is magic and rare amulets: with none built
yet, the offer still counts as taken."""
QUEST = "a3q4"
DONE = "a3q4-QuestRewardTaken"
REQUIRES = ["a3q2"]

HAVE = "a3q4-HaveRibbonSpool"
DELIVERED = "a3q4-DeliveredSpool"

STEPS = [
    {"verb": "talk", "npc": "Grigor", "option": "The Gemling Queen", "area": "1_3_town", "skip_if": ["a3q3-QuestReceived", "a3q3-DiallaIntroduced"],
     "optional": True},
    {"verb": "goto", "area": "1_3_7", "skip_if": [HAVE, DELIVERED]},
    {"verb": "interact", "target": "RibbonSpoolChest", "skip_if": [HAVE, DELIVERED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/RibbonSpool", "expect_flag": HAVE, "skip_if": [HAVE, DELIVERED]},
    {"verb": "goto", "area": "1_3_8_1", "skip_if": ["Visited-3_8_2", DONE]},
    {"verb": "goto", "area": "1_3_8_2", "skip_if": DONE},
    {"verb": "talk", "npc": "Lady Dialla", "option": "Introduction", "area": "1_3_8_2", "expect_flag": "a3q3-DiallaIntroduced",
     "skip_if": "a3q3-DiallaIntroduced"},
    # 439 gives the quest, 440/441 take the spool: the same text, the next row each time.
    {"verb": "talk", "npc": "Lady Dialla", "option": "The Ribbon Spool", "area": "1_3_8_2", "skip_if": [DELIVERED, DONE]},
    {"verb": "talk", "npc": "Lady Dialla", "option": "The Ribbon Spool", "area": "1_3_8_2", "expect_flag": DELIVERED, "skip_if": [DELIVERED, DONE]},
    {"verb": "reward", "npc": "Lady Dialla", "option": "Ribbon Spool Reward", "area": "1_3_8_2", "pick": ["Amulet"], "skip_if": DONE},
    {"verb": "wait_flag", "flag": DONE, "timeout": 10},
]
