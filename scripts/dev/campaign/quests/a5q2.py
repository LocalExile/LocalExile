"""a5q2 "The Key to Freedom" (act5.md): Utula's task; Justicar Casticus in The Control Blocks drops the Eyes of Zeal;
Lani's reward (offer 35: a normal Diamond, Granite or Jade Flask can be built)."""
QUEST = "a5q2"
DONE = "a5q2-QuestRewardTaken"
REQUIRES = ["a5q1b"]

STEPS = [
    {"verb": "talk", "npc": "Act5/Utula", "option": "Introduction", "area": "1_5_town", "skip_if": ["UtulaIntroduction", "a5q2-KilledBoss"],
     "optional": True},
    {"verb": "talk", "npc": "Act5/Utula", "option": "Justicar Casticus", "index": 1, "area": "1_5_town", "expect_flag": "a5q2-QuestReceived",
     "skip_if": ["a5q2-QuestReceived", "a5q2-KilledBoss"], "optional": True},
    {"verb": "goto", "area": "1_5_2", "skip_if": ["a5q2-HaveKey", "a5q2-UsedKey"]},
    {"verb": "kill", "target": "Bloodwing", "appear_timeout": 15, "expect_flag": "a5q2-KilledBoss",
     "skip_if": ["a5q2-KilledBoss", "a5q2-HaveKey", "a5q2-UsedKey"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act5/TemplarCourtKey", "expect_flag": "a5q2-HaveKey",
     "skip_if": ["a5q2-HaveKey", "a5q2-UsedKey"]},
    {"verb": "talk", "npc": "Lani", "option": "Justicar Casticus", "area": "1_5_town", "expect_flag": "a5q2-Congratulated",
     "skip_if": ["a5q2-Congratulated", DONE]},
    {"verb": "reward", "npc": "Lani", "option": "Casticus Reward", "area": "1_5_town", "pick": ["Diamond Flask", "Granite Flask", "Jade Flask"],
     "skip_if": DONE},
]
