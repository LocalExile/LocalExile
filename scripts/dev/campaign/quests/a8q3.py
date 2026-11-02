"""a8q3 "Solar Eclipse" with a8q2 "Lunar Eclipse" (act8.md): Maramoa's orbs; Dusk and Dawn drop them in the two
temples; both used at the fight starter on The Harbour Bridge bring Solaris and Lunaris; both dead: a8q23-KilledBosses
(Act 9 and the Pantheon's Solaris and Lunaris). Sin (1229: the Pantheon panel)."""
QUEST = "a8q3"
DONE = "a8q23-KilledBosses"
REQUIRES = ["a8q7"]

STEPS = [
    {"verb": "talk", "npc": "Act8/Maramoa", "option": "Solaris and Lunaris", "area": "2_8_town", "expect_flag": "a8q23-QuestsReceived", "skip_if": ["a8q23-QuestsReceived", "a8q23-KilledBosses"]},
    {"verb": "talk", "npc": "Act8/Maramoa", "option": "The Moon Orb", "area": "2_8_town", "skip_if": ["MaramoaOnMoonOrb", "a8q23-DeliveredItems"], "optional": True},
    {"verb": "talk", "npc": "Act8/Maramoa", "option": "The Sun Orb", "area": "2_8_town", "skip_if": ["MaramoaOnSunOrb", "a8q23-DeliveredItems"], "optional": True},
    {"verb": "goto", "area": "2_8_7_2", "skip_if": ["a8q2-HaveItem", "a8q23-DeliveredItems"]},
    {"verb": "kill", "target": "GuardianOfLunaris/GuardianOfLunaris", "area": "2_8_7_2", "appear_timeout": 15, "skip_if": ["a8q2-HaveItem", "a8q23-DeliveredItems"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act8/LunarisOrb", "expect_flag": "a8q2-HaveItem", "skip_if": ["a8q2-HaveItem", "a8q23-DeliveredItems"]},
    {"verb": "goto", "area": "2_8_12_2", "skip_if": ["a8q3-HaveItem", "a8q23-DeliveredItems"]},
    {"verb": "kill", "target": "GuardianOfSolaris/GuardianOfSolaris", "area": "2_8_12_2", "appear_timeout": 15, "skip_if": ["a8q3-HaveItem", "a8q23-DeliveredItems"]},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act8/SolarisOrb", "expect_flag": "a8q3-HaveItem", "skip_if": ["a8q3-HaveItem", "a8q23-DeliveredItems"]},
    {"verb": "goto", "area": "2_8_13"},
    {"verb": "interact", "target": "GoddessFightStarter", "area": "2_8_13", "expect_flag": "a8q23-DeliveredItems", "skip_if": "a8q23-DeliveredItems"},
    {"verb": "kill", "target": "LunarisSolaris/", "area": "2_8_13", "appear_timeout": 15, "expect_flag": "a8q23-KilledBosses"},
    {"verb": "talk", "npc": "SinSolarisLunaris", "option": "Solaris and Lunaris", "area": "2_8_13", "expect_flag": "SinSolarisLunarisReward", "skip_if": "SinSolarisLunarisReward", "optional": True},
    {"verb": "pantheon", "optional": True},
]
