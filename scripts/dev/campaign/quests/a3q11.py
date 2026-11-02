"""a3q11 "Victario's Secrets": Hargan wants Victario's three Platinum Busts, hidden in stashes in The Sewers. He takes
them ("Victario's Holdfasts") and gives a Book of Skill: one passive and two refund points once used."""
QUEST = "a3q11"
DONE = "a3q11-UsedRewardBook"
REQUIRES = ["a3q2"]

DELIVERED = ["a3q11-DeliveredBust1", "a3q11-DeliveredBust2", "a3q11-DeliveredBust3"]
BOOK = "a3q11-HaveRewardBook"


def bust(n):
    have, done = f"a3q11-HaveBust{n}", f"a3q11-DeliveredBust{n}"
    return [
        {"verb": "interact", "target": f"Victario/Stash{n}", "area": "1_3_10_1", "skip_if": [have, done, BOOK, DONE]},
        {"verb": "pickup", "item": f"Metadata/Items/QuestItems/Busts/Bust{n}", "expect_flag": have, "skip_if": [have, done, BOOK, DONE]},
    ]


STEPS = [
    {"verb": "talk", "npc": "Hargan", "option": "Introduction", "area": "1_3_town", "skip_if": ["HarganIntroduced", BOOK, DONE],
     "optional": True},
    {"verb": "talk", "npc": "Hargan", "option": "Victario", "area": "1_3_town", "expect_flag": "a3q11-QuestReceived",
     "skip_if": ["a3q11-QuestReceived", "a3q11-Congratulated", BOOK, DONE], "optional": True},
    *bust(1), *bust(2), *bust(3),
    {"verb": "talk", "npc": "Hargan", "option": "Victario's Holdfasts", "area": "1_3_town", "expect_flag": "a3q11-Congratulated",
     "skip_if": ["a3q11-Congratulated", BOOK, DONE]},
    {"verb": "reward", "npc": "Hargan", "option": "Platinum Bust Reward", "area": "1_3_town", "expect_flag": BOOK, "skip_if": [BOOK, DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/SkillBooks/Book-a3q11v2", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
