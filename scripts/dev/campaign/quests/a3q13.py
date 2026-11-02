"""a3q13 "A Swig of Hope": Captain Fairgraves (in The Docks only when he died in Act 1, a1q6-KilledFairgraves) wants the
Decanter Spiritus (the Ornate Chest in The Marketplace) and Chitus' Plum (the plum tree in The Imperial Gardens). His
rows all read "A Swig of Hope": each talk picks the next one until the items are delivered. His reward (offer 25) is
magic and rare rings: with none built yet, the offer still counts as taken."""
QUEST = "a3q13"
DONE = "a3q13-QuestRewardTaken"
REQUIRES = ["a3q5"]

DELIVERED = "a3q13-DeliveredItems"
FAIRGRAVES = "Metadata/NPC/Act3/Fairgraves"

STEPS = [
    {"verb": "goto", "area": "1_3_5", "skip_if": ["a3q13-HaveDecanter", "a3q13-DecanterDelivered", DELIVERED, DONE]},
    {"verb": "interact", "target": "DecanterChest", "skip_if": ["a3q13-HaveDecanter", "a3q13-DecanterDelivered", DELIVERED, DONE]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Fairgraves/Decanter", "expect_flag": "a3q13-HaveDecanter",
     "skip_if": ["a3q13-HaveDecanter", "a3q13-DecanterDelivered", DELIVERED, DONE]},
    {"verb": "goto", "area": "1_3_15", "skip_if": ["a3q13-HavePlum", "a3q13-PlumDelivered", DELIVERED, DONE]},
    {"verb": "interact", "target": "FruitTree", "skip_if": ["a3q13-HavePlum", "a3q13-PlumDelivered", DELIVERED, DONE]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Fairgraves/Fruit", "expect_flag": "a3q13-HavePlum",
     "skip_if": ["a3q13-HavePlum", "a3q13-PlumDelivered", DELIVERED, DONE]},
    {"verb": "goto", "area": "1_3_9", "skip_if": DONE},
    {"verb": "talk", "npc": FAIRGRAVES, "option": "Introduction", "area": "1_3_9", "skip_if": ["a3q13-FairgravesIntro", DONE], "optional": True},
    *[{"verb": "talk", "npc": FAIRGRAVES, "option": "A Swig of Hope", "area": "1_3_9", "skip_if": [DELIVERED, DONE]} for _ in range(5)],
    {"verb": "reward", "npc": FAIRGRAVES, "option": "Swig of Hope Reward", "area": "1_3_9", "pick": ["Ring"], "skip_if": DONE},
    {"verb": "wait_flag", "flag": DONE, "timeout": 10},
]
