"""a6q6 "The Puppet Mistress" (act6.md): Ryslatha in The Wetlands; Sin (1000: the Pantheon panel); Tarkleigh's Book of
Skill (offer 44; QuestStaticRewards 15: +1 passive)."""
QUEST = "a6q6"
DONE = "a6q6-UsedRewardBook"
REQUIRES = ["a6q2"]

KILLED = "a6q6-KilledBoss"

STEPS = [
    {"verb": "talk", "npc": "Act6/Tarkleigh", "option": "The Puppet Mistress", "index": 16, "area": "2_6_town", "expect_flag": "a6q6-QuestReceived",
     "skip_if": ["a6q6-QuestReceived", KILLED], "optional": True},
    {"verb": "goto", "area": "2_6_11", "skip_if": KILLED},
    {"verb": "kill", "target": "InsectSpawnerParasiteBoss", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "talk", "npc": "SinRyslatha", "option": "Ryslatha", "area": "2_6_11", "expect_flag": "SinRyslathaReward",
     "skip_if": "SinRyslathaReward", "optional": True},
    {"verb": "talk", "npc": "Act6/Tarkleigh", "option": "The Puppet Mistress", "index": 17, "area": "2_6_town", "expect_flag": "a6q6-Congratulated",
     "skip_if": ["a6q6-Congratulated", "a6q6-HaveRewardBook", DONE]},
    {"verb": "pantheon", "optional": True},
    {"verb": "reward", "npc": "Act6/Tarkleigh", "option": "Puppet Mistress Reward", "area": "2_6_town", "skip_if": ["a6q6-HaveRewardBook", DONE]},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act6/Book-a6q6", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
