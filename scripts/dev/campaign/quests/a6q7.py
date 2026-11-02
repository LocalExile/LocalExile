"""a6q7 "The Cloven One" (act6.md), with a6q1's second Nessa at Prisoner's Gate: Abberath fights in two phases (the
second appears where the first falls); Sin (997: the Pantheon panel); Bestel's Book of Skill (offer 45; QuestStaticRewards
17: +1 passive)."""
QUEST = "a6q7"
DONE = "a6q7-UsedRewardBook"
REQUIRES = ["a6q2"]

KILLED = "a6q7-KilledBoss"

STEPS = [
    {"verb": "talk", "npc": "Act6/Bestel", "option": "Abberath", "index": 10, "area": "2_6_town", "expect_flag": "a6q7-QuestReceived",
     "skip_if": ["a6q7-QuestReceived", KILLED], "optional": True},
    {"verb": "goto", "area": "2_6_8", "skip_if": [KILLED, "a6q1-SeenNessa2"]},
    {"verb": "tp", "target": "NessaPrisonersGate", "expect_flag": "a6q1-SeenNessa2", "skip_if": "a6q1-SeenNessa2", "optional": True},
    {"verb": "goto", "area": "2_6_8", "skip_if": KILLED},
    {"verb": "kill", "target": "Abberath/Abberath", "appear_timeout": 15, "skip_if": KILLED},
    {"verb": "kill", "target": "Abberath/Abberath2", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "talk", "npc": "SinAbberath", "option": "Abberath", "area": "2_6_8", "expect_flag": "SinAberrathReward",
     "skip_if": "SinAberrathReward", "optional": True},
    {"verb": "talk", "npc": "Act6/Bestel", "option": "Abberath", "index": 11, "area": "2_6_town", "expect_flag": "a6q7-Congratulated",
     "skip_if": ["a6q7-Congratulated", "a6q7-HaveRewardBook", DONE]},
    {"verb": "pantheon", "optional": True},
    {"verb": "reward", "npc": "Act6/Bestel", "option": "Abberath Reward", "area": "2_6_town", "skip_if": ["a6q7-HaveRewardBook", DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act6/Book-a6q7", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
