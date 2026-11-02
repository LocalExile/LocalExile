"""a4q3 "The King of Fury" (act4.md): Dialla (Highgate) sends the player to the Rapture Device in The Crystal Veins,
where Dialla asks for the two Eyes (a4q1-SeenRaptureDialla, a4q1-GemQuestsReceived); Kaom in his Stronghold drops
The Eye of Fury, handed in to Dialla at the Rapture (NPCTalk 530). Kaom's Stronghold is reached through the Crystal
Veins' dream portal and Kaom's Dream."""
QUEST = "a4q3"
DONE = "a4q3-DeliveredGem"
REQUIRES = ["a4q2"]

GEMS = "a4q1-GemQuestsReceived"

STEPS = [
    {"verb": "talk", "npc": "DiallaTown", "option": "Malachai", "index": 1, "area": "1_4_town", "expect_flag": "a4q1-FindRapture",
     "skip_if": ["a4q1-FindRapture", GEMS]},
    {"verb": "goto", "area": "1_4_3_3", "skip_if": GEMS},
    {"verb": "tp", "target": "DiallaRapture", "expect_flag": "a4q1-SeenRaptureDialla", "skip_if": ["a4q1-SeenRaptureDialla", GEMS]},
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Rapture", "index": 0, "area": "1_4_3_3", "expect_flag": GEMS, "skip_if": GEMS},
    {"verb": "goto", "area": "1_4_4_3", "skip_if": ["a4q3-HaveGem", DONE]},
    {"verb": "kill", "target": "KaomBoss", "appear_timeout": 15, "expect_flag": "a4q3-KilledKaom", "skip_if": ["a4q3-KilledKaom", "a4q3-HaveGem", DONE]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/KaomGem", "expect_flag": "a4q3-HaveGem", "skip_if": ["a4q3-HaveGem", DONE]},
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Eye of Fury", "index": 4, "area": "1_4_3_3", "expect_flag": DONE, "skip_if": DONE},
]
