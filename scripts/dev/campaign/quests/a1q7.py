"""a1q7 "The Dweller of the Deep": Tarkleigh asks for the giant crab in The Flooded Depths (below The Submerged
Passage). His reward is a Book of Skill: one passive point once used."""
QUEST = "a1q7"
DONE = "a1q7-UsedRewardBook"
REQUIRES = ["a1q4"]

KILLED = "a1q7-KilledDweller"
BOOK = "a1q7-HaveRewardBook"

STEPS = [
    {"verb": "talk", "npc": "Tarkleigh", "option": "Dweller of the Deep", "area": "1_1_town", "expect_flag": "a1q7-ReceivedQuest",
     "skip_if": ["a1q7-ReceivedQuest", KILLED], "optional": True},
    {"verb": "goto", "area": "1_1_4_1", "skip_if": KILLED},
    {"verb": "goto", "area": "1_1_4_0", "skip_if": KILLED},
    {"verb": "kill", "target": "Dweller of the Deep", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    # After the kill Tarkleigh has a second "Dweller of the Deep" row (NPCTalk 42, a1q7-Congratulated).
    {"verb": "talk", "npc": "Tarkleigh", "option": "Dweller of the Deep", "area": "1_1_town", "expect_flag": "a1q7-Congratulated",
     "skip_if": ["a1q7-Congratulated", BOOK, DONE]},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Dweller Reward", "area": "1_1_town", "expect_flag": BOOK, "skip_if": [BOOK, DONE]},
    {"verb": "use", "item": "Metadata/Items/QuestItems/SkillBooks/Book-a1q7", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
