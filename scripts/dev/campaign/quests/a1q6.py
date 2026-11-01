"""a1q6 "The Marooned Mariner": Captain Fairgraves on his ship in The Ship Graveyard wants the Allflame; the wounded
slave girl (a quest chest in The Ship Graveyard Cave's treasure room) has it. "Thank You" hands it over and turns him
into the boss; his death sets a1q6-KilledFairgraves. Bestel's reward is a Book of Skill: one passive point once used."""
QUEST = "a1q6"
DONE = "a1q6-UsedRewardBook"
REQUIRES = ["a1q2"]

KILLED = "a1q6-KilledFairgraves"
DELIVERED = "a1q6-DeliveredAllFlame"
BOOK = "a1q6-HaveRewardBook"

STEPS = [
    {"verb": "goto", "area": "1_1_8", "skip_if": [KILLED, "a1q6-ReceivedQuest"]},
    {"verb": "goto", "area": "1_1_9", "skip_if": KILLED},
    {"verb": "talk", "npc": "Fairgraves", "option": "Introduction", "skip_if": ["a1q6-MetFairgraves", KILLED], "optional": True},
    {"verb": "talk", "npc": "Fairgraves", "option": "Marooned", "expect_flag": "a1q6-ReceivedQuest",
     "skip_if": ["a1q6-ReceivedQuest", KILLED]},
    {"verb": "goto", "area": "1_1_9a", "skip_if": ["a1q6-HaveAllFlame", DELIVERED, KILLED]},
    {"verb": "interact", "target": "AllFlameSlaveGirl", "skip_if": ["a1q6-HaveAllFlame", DELIVERED, KILLED]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/AllFlameLantern1", "expect_flag": "a1q6-HaveAllFlame",
     "skip_if": ["a1q6-HaveAllFlame", DELIVERED, KILLED]},
    {"verb": "goto", "area": "1_1_9", "skip_if": KILLED},
    # "Thank You" (NPCTalk 170, or 171 when the Allflame was given before): Fairgraves becomes the boss.
    {"verb": "talk", "npc": "Fairgraves", "option": "Thank You", "skip_if": KILLED},
    {"verb": "kill", "target": "SkeletonBossFairgraves", "appear_timeout": 15, "expect_flag": KILLED, "skip_if": KILLED},
    {"verb": "talk", "npc": "Bestel", "option": "Captain Fairgraves", "area": "1_1_town", "expect_flag": "a1q6-Congratulated",
     "skip_if": ["a1q6-Congratulated", BOOK, DONE]},
    {"verb": "reward", "npc": "Bestel", "option": "Fairgraves Reward", "area": "1_1_town", "expect_flag": BOOK, "skip_if": [BOOK, DONE]},
    {"verb": "use", "item": "Metadata/Items/QuestItems/SkillBooks/Book-a1q6", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "passives"},
]
