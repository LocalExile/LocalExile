"""a9q4 "Fastis Fortuna" (act9.md): Petarus and Vanja's quest; Boulderback dies in The Foothills and drops the Maraketh
Calendar; handed in, their Book of Regrets (offer 61: 2 respec)."""
QUEST = "a9q4"
DONE = "a9q4-UsedRewardBook"
REQUIRES = ["a9q3"]

STEPS = [
    {"verb": "talk", "npc": "PetarusVanja", "option": "The Maraketh Calendar", "area": "2_9_town", "expect_flag": "a9q4-QuestReceived", "skip_if": ["a9q4-QuestReceived", "a9q4-HaveItem", "a9q4-DeliveredItem"]},
    {"verb": "goto", "area": "2_9_5", "skip_if": ["a9q4-HaveItem", "a9q4-DeliveredItem"]},
    {"verb": "kill", "target": "BullBossFoothills", "area": "2_9_5", "appear_timeout": 15, "skip_if": ["a9q4-HaveItem", "a9q4-DeliveredItem"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act9/Calendar", "expect_flag": "a9q4-HaveItem", "skip_if": ["a9q4-HaveItem", "a9q4-DeliveredItem"]},
    {"verb": "talk", "npc": "PetarusVanja", "option": "The Maraketh Calendar", "area": "2_9_town", "expect_flag": "a9q4-DeliveredItem", "skip_if": ["a9q4-DeliveredItem", "a9q4-HaveRewardBook"]},
    {"verb": "reward", "npc": "PetarusVanja", "option": "Maraketh Calendar Reward", "area": "2_9_town", "skip_if": "a9q4-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act9/Book-a9q4", "expect_flag": "a9q4-UsedRewardBook"},
]
