"""a1q1 "Enemy at the Gate", with the opening encounter on The Twilight Strand: pick up and wield the class weapon,
talk to the Dying Exile (the bite scene, about 23 s), kill the zombie for the skill gem, open the support gem chest,
kill Hillock, enter Lioneye's Watch and take Tarkleigh's reward. The opening steps are optional: a character that
already did them (the save keeps the tutorial, not flags) just warns and goes on."""
QUEST = "a1q1"
DONE = "a1q1-QuestRewardTaken"

# Every class's start weapon (BaseItemTypes class names).
WEAPONS = ["Wand", "Sword", "Mace", "Sceptre", "Axe", "Bow", "Claw", "Dagger", "Staff"]
PAST_OPENING = ["a1q1-SeenHillock", "a1q1-KilledHillock"]

STEPS = [
    {"verb": "goto", "area": "1_1_1", "skip_if": "a1q1-KilledHillock"},
    {"verb": "pickup", "item": WEAPONS, "skip_if": ["TalkedToWoundedExile", *PAST_OPENING], "optional": True},
    {"verb": "equip", "item": WEAPONS, "skip_if": ["TalkedToWoundedExile", *PAST_OPENING], "optional": True},
    {"verb": "interact", "target": "WoundedExile", "expect_flag": "TalkedToWoundedExile",
     "skip_if": ["TalkedToWoundedExile", *PAST_OPENING], "optional": True},
    {"verb": "kill", "target": "ZombieBite", "appear_timeout": 40, "skip_if": PAST_OPENING, "optional": True},
    {"verb": "pickup", "item": "Active Skill Gem", "skip_if": PAST_OPENING, "optional": True},
    {"verb": "interact", "target": "TutorialSupportGemChest", "skip_if": PAST_OPENING, "optional": True},
    {"verb": "pickup", "item": "Support Skill Gem", "skip_if": PAST_OPENING, "optional": True},
    {"verb": "kill", "target": "Hillock", "expect_flag": "a1q1-KilledHillock", "skip_if": "a1q1-KilledHillock"},
    {"verb": "goto", "area": "1_1_town", "via": "transition"},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Hillock Reward"},
    {"verb": "passives"},
]
