"""a2q7 "Deal with the Bandits", the automation's choice: kill all three bandit lords (a2q1-a2q3 kill them and pick up
their amulets). Eramir takes the three amulets ("Bandit Amulets": one passive point, QuestStaticRewards 8), combines
them and gives The Apex (offer 17), which a2q8 needs for the Dark Altar."""
QUEST = "a2q7"
DONE = "a2q7-QuestRewardTaken"
REQUIRES = ["a2q1", "a2q2", "a2q3"]

DELIVERED = "a2b-DeliveredAmulets"
APEX = ["a2q8-HaveApex", "a2q8-PlacedApex"]

STEPS = [
    {"verb": "talk", "npc": "Eramir", "option": "The Bandit Scourge", "area": "1_2_town", "skip_if": ["a2q7-ReceivedQuest", "a2b-Congratulated", DELIVERED],
     "optional": True},
    {"verb": "talk", "npc": "Eramir", "option": "Bandit Lords", "area": "1_2_town", "expect_flag": "a2b-Congratulated",
     "skip_if": ["a2b-Congratulated", DELIVERED]},
    # NPCTalk 95: SetQuestFlag("a2q7-QuestRewardTaken", "a2b-DeliveredAmulets"); the amulets go with the second flag.
    {"verb": "talk", "npc": "Eramir", "option": "Bandit Amulets", "area": "1_2_town", "expect_flag": DONE, "skip_if": DONE},
    {"verb": "talk", "npc": "Eramir", "option": "Bandit Amulets", "area": "1_2_town", "expect_flag": "a2q7-EramirCombined",
     "skip_if": ["a2q7-EramirCombined", *APEX], "optional": True},
    {"verb": "reward", "npc": "Eramir", "option": "Take the Apex", "area": "1_2_town", "pick": ["The Apex"], "expect_flag": "a2q8-HaveApex",
     "skip_if": APEX},
    {"verb": "passives"},
]
