"""a3q12 "A Fixture of Fate": Siosa in The Library wants the four Golden Pages from the book stands of The Archives
(behind the Library's hidden door, which a loose candle opens). He takes them and rewards (offer 24, gems and an Uncarved Gemstone)."""
QUEST = "a3q12"
DONE = "a3q12-QuestRewardTaken"
REQUIRES = ["a3q5"]

DELIVERED = "a3q12-DeliveredPages"


def page(n):
    have = f"a3q12-HavePage{n}"
    return [
        {"verb": "interact", "target": f"GoldenBookStand{n}", "area": "1_3_17_2", "skip_if": [have, DELIVERED, DONE]},
        {"verb": "pickup", "item": f"Metadata/Items/QuestItems/GoldenPages/Page{n}", "expect_flag": have, "skip_if": [have, DELIVERED, DONE]},
    ]


STEPS = [
    {"verb": "goto", "area": "1_3_17_1", "skip_if": DONE},
    {"verb": "talk", "npc": "Siosa", "option": "Introduction", "area": "1_3_17_1", "skip_if": ["SiosaIntroduced", DELIVERED, DONE], "optional": True},
    {"verb": "talk", "npc": "Siosa", "option": "Four Golden Pages", "area": "1_3_17_1", "expect_flag": "a3q12-QuestReceived",
     "skip_if": ["a3q12-QuestReceived", DELIVERED, DONE], "optional": True},
    # The loose candle opens the hidden door (for this instance); its transition leads into The Archives.
    {"verb": "interact", "target": "HiddenDoorTrigger", "area": "1_3_17_1", "skip_if": [DELIVERED, DONE]},
    {"verb": "goto", "area": "1_3_17_2", "via": "transition", "skip_if": [DELIVERED, DONE]},
    *page(1), *page(2), *page(3), *page(4),
    {"verb": "talk", "npc": "Siosa", "option": "Four Golden Pages", "area": "1_3_17_1", "expect_flag": DELIVERED, "skip_if": [DELIVERED, DONE]},
    {"verb": "talk", "npc": "Siosa", "option": "Four Golden Pages", "area": "1_3_17_1", "expect_flag": "a3q12-Congratulated",
     "skip_if": ["a3q12-Congratulated", DONE], "optional": True},
    {"verb": "reward", "npc": "Siosa", "option": "Golden Pages Reward", "area": "1_3_17_1", "skip_if": DONE},
    {"verb": "passives"},
]
