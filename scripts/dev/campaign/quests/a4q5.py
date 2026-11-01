"""a4q5 "Corpus Malachus" (act4.md), with a4q1's middle: Dialla activates the Rapture Device (NPCTalk 534) and opens the
wound into the Belly (535); Piety, the Abomination dies in The Belly of the Beast Level 2 and stays as PietyBelly, whose
"Malachai" (621) opens The Harvest; the three guardians drop Malachai's organs, which Piety takes (624-626, by option
index: the rows share their text) and opens the Black Core (627)."""
QUEST = "a4q5"
DONE = "a4q5-QuestComplete"
REQUIRES = ["a4q3", "a4q4"]

RECEIVED = "a4q5-QuestReceived"
ORGANS = [("Doedre/Doedre", "Organ1", "a4q5-HaveOrgan1", "a4q5-DeliveredOrgan1", 1),
          ("Maligaro/Maligaro", "Organ2", "a4q5-HaveOrgan2", "a4q5-DeliveredOrgan2", 2),
          ("Shavronne/Shavronne", "Organ3", "a4q5-HaveOrgan3", "a4q5-DeliveredOrgan3", 3)]

STEPS = [
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Rapture", "index": 8, "area": "1_4_3_3", "expect_flag": "a4q1-RaptureActivated",
     "skip_if": ["a4q1-RaptureActivated", RECEIVED]},
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Rapture", "index": 9, "area": "1_4_3_3", "expect_flag": "a4q1-DiallaOnTransformed",
     "skip_if": ["a4q1-DiallaOnTransformed", RECEIVED]},
    {"verb": "goto", "area": "1_4_6_1", "skip_if": ["a4q1-KilledPiety", RECEIVED]},
    {"verb": "goto", "area": "1_4_6_2", "skip_if": RECEIVED},
    {"verb": "kill", "target": "PietyBeastBoss", "appear_timeout": 15, "expect_flag": "a4q1-KilledPiety", "skip_if": ["a4q1-KilledPiety", RECEIVED]},
    {"verb": "talk", "npc": "PietyBelly", "option": "Nightmare", "area": "1_4_6_2", "expect_flag": "a4q5-PietyThanks",
     "skip_if": ["a4q5-PietyThanks", RECEIVED]},
    {"verb": "talk", "npc": "PietyBelly", "option": "Malachai", "area": "1_4_6_2", "expect_flag": RECEIVED, "skip_if": RECEIVED},
    {"verb": "goto", "area": "1_4_6_3", "skip_if": DONE},
]
for variety, organ, have, delivered, index in ORGANS:
    STEPS += [
        {"verb": "kill", "target": variety, "area": "1_4_6_3", "appear_timeout": 15, "skip_if": [have, delivered, DONE]},
        {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/" + organ, "area": "1_4_6_3", "expect_flag": have,
         "skip_if": [have, delivered, DONE]},
    ]
for variety, organ, have, delivered, index in ORGANS:
    STEPS.append({"verb": "talk", "npc": "PietyHarvest", "option": "Malachai's Organs", "index": index, "area": "1_4_6_3",
                  "expect_flag": delivered, "skip_if": [delivered, DONE]})
STEPS.append({"verb": "talk", "npc": "PietyHarvest", "option": "Malachai's Organs", "index": 4, "area": "1_4_6_3", "expect_flag": DONE,
              "skip_if": DONE})
