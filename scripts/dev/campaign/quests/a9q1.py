"""a9q1 "Recurring Nightmare" (act9.md), Act 9 -> 10: Sin's "The Dark Ember"; General Adus' death opens the Theurgic
Precipitate Machine (Trarthan Powder), The Basilisk drops its acid; Sin takes both, SinQuarry burns the membrane
(SinEnterBeast); in The Rotting Core SinCore opens the heart portals, the three spirits die, "United Trio", The Depraved
Trinity dies (a9q1-KilledBoss); Lilly sails to Oriath Docks (1395, AreaTransitionTo)."""
QUEST = "a9q1"
DONE = "Visited-10_town"
REQUIRES = ["a9q2"]

STEPS = [
    {"verb": "talk", "npc": "Act9/SinTown", "option": "The Dark Ember", "area": "2_9_town", "expect_flag": "a9q1-QuestReceived", "skip_if": ["a9q1-QuestReceived", "a9q1-QuarryClue", "SinEnterBeast"]},
    {"verb": "goto", "area": "2_9_9", "skip_if": ["a9q1-HaveItem1", "a9q1-DeliveredItem1"]},
    {"verb": "kill", "target": "GeneralAdus/GeneralAdus", "area": "2_9_9", "appear_timeout": 15, "expect_flag": "a9q1-KilledAdus", "skip_if": ["a9q1-KilledAdus", "a9q1-HaveItem1", "a9q1-DeliveredItem1"]},
    {"verb": "interact", "target": "TheurgicPrecipitate", "area": "2_9_9", "skip_if": ["a9q1-HaveItem1", "a9q1-DeliveredItem1"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act9/Ingredient1", "expect_flag": "a9q1-HaveItem1", "skip_if": ["a9q1-HaveItem1", "a9q1-DeliveredItem1"]},
    {"verb": "goto", "area": "2_9_6", "skip_if": ["a9q1-HaveItem2", "a9q1-DeliveredItem2"]},
    {"verb": "kill", "target": "IguanaBossPhoenix", "area": "2_9_6", "appear_timeout": 15, "skip_if": ["a9q1-HaveItem2", "a9q1-DeliveredItem2"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act9/Ingredient2", "expect_flag": "a9q1-HaveItem2", "skip_if": ["a9q1-HaveItem2", "a9q1-DeliveredItem2"]},
    {"verb": "talk", "npc": "Act9/SinTown", "option": "_take_ingredients_", "area": "2_9_town", "expect_flag": "a9q1-DeliveredItem1", "skip_if": "a9q1-DeliveredItem1"},
    {"verb": "talk", "npc": "Act9/SinTown", "option": "_take_ingredient2_", "area": "2_9_town", "expect_flag": "a9q1-DeliveredItem2", "skip_if": "a9q1-DeliveredItem2"},
    {"verb": "talk", "npc": "Act9/SinTown", "option": "The Dark Ember", "area": "2_9_town", "skip_if": ["a9q1-QuarryClue", "SinEnterBeast"], "optional": True},
    {"verb": "goto", "area": "2_9_8", "skip_if": "SinEnterBeast"},
    {"verb": "talk", "npc": "SinQuarry", "option": "The Beast", "area": "2_9_8", "expect_flag": "SinEnterBeast", "skip_if": "SinEnterBeast"},
    {"verb": "goto", "area": "2_9_10_1", "via": "transition", "skip_if": ["Visited-9_10_1", "a9q1-KilledBoss"]},
    {"verb": "goto", "area": "2_9_10_2", "skip_if": "a9q1-KilledBoss"},
    {"verb": "talk", "npc": "SinCore", "option": "The Dark Ember", "area": "2_9_10_2", "skip_if": "a9q1-KilledBoss", "optional": True},
    {"verb": "kill", "target": "DoedreSoul", "area": "2_9_10_2", "appear_timeout": 15, "expect_flag": "a9q1-KilledDoedre", "skip_if": ["a9q1-KilledDoedre", "a9q1-KilledBoss"]},
    {"verb": "kill", "target": "MaligaroSoul", "area": "2_9_10_2", "appear_timeout": 15, "expect_flag": "a9q1-KilledMaligaro", "skip_if": ["a9q1-KilledMaligaro", "a9q1-KilledBoss"]},
    {"verb": "kill", "target": "ShavronneSoul", "area": "2_9_10_2", "appear_timeout": 15, "expect_flag": "a9q1-KilledShavronne", "skip_if": ["a9q1-KilledShavronne", "a9q1-KilledBoss"]},
    {"verb": "talk", "npc": "SinCore", "option": "United Trio", "area": "2_9_10_2", "skip_if": "a9q1-KilledBoss", "optional": True},
    {"verb": "kill", "target": "UnholyTrio/UnholyTrio", "area": "2_9_10_2", "appear_timeout": 15, "expect_flag": "a9q1-KilledBoss", "skip_if": "a9q1-KilledBoss"},
    {"verb": "talk", "npc": "Act9/Lilly", "option": "Oriath", "area": "2_9_10_2", "skip_if": "LillyOnOriath", "optional": True},
    {"verb": "talk", "npc": "Act9/Lilly", "option": "Sail to Oriath", "area": "2_9_10_2", "no_wait": True},
    {"verb": "wait_flag", "flag": "Visited-10_town", "timeout": 60},
]
