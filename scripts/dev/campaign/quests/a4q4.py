"""a4q4 "The King of Desire" (act4.md): Daresso in The Grand Arena drops The Eye of Desire, handed in to Dialla at the
Rapture (NPCTalk 531). The Grand Arena is reached through the Crystal Veins' dream portal and Daresso's Dream."""
QUEST = "a4q4"
DONE = "a4q4-DeliveredGem"
REQUIRES = ["a4q3"]

STEPS = [
    {"verb": "goto", "area": "1_4_5_2", "skip_if": ["a4q4-HaveGem", DONE]},
    {"verb": "kill", "target": "DaressoBoss", "appear_timeout": 15, "expect_flag": "a4q4-KilledDaresso",
     "skip_if": ["a4q4-KilledDaresso", "a4q4-HaveGem", DONE]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/DaressoGem", "expect_flag": "a4q4-HaveGem", "skip_if": ["a4q4-HaveGem", DONE]},
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Eye of Desire", "index": 5, "area": "1_4_3_3", "expect_flag": DONE, "skip_if": DONE},
]
