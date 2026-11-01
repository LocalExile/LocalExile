"""a9q5 "Queen of the Sands" (act9.md): Sin's "Shakari", Petarus and Vanja's Bottled Storm (offer 63); carried to the
sand barrier in The Vastiri Desert it clears the storm (a9q5-UsedItem: The Oasis opens); Shakari's second stage dies,
Sin (1383: the Pantheon panel), Irasha's book (offer 62)."""
QUEST = "a9q5"
DONE = "a9q5-UsedRewardBook"
REQUIRES = ["a9q3"]

STEPS = [
    {"verb": "talk", "npc": "Act9/SinTown", "option": "Shakari", "area": "2_9_town", "expect_flag": "a9q5-QuestReceived", "skip_if": ["a9q5-QuestReceived", "a9q5-KilledBoss"]},
    {"verb": "talk", "npc": "PetarusVanja", "option": "Bottled Storm", "area": "2_9_town", "expect_flag": "a9q5-StormClue", "skip_if": ["a9q5-StormClue", "a9q5-HaveItem", "a9q5-UsedItem"]},
    {"verb": "reward", "npc": "PetarusVanja", "option": "Take Bottled Storm", "area": "2_9_town", "pick": ["Bottled Storm"], "skip_if": ["a9q5-HaveItem", "a9q5-UsedItem"]},
    {"verb": "goto", "area": "2_9_3", "skip_if": "a9q5-UsedItem"},
    {"verb": "tp", "target": "OasisSandBarrier", "area": "2_9_3", "expect_flag": "a9q5-UsedItem", "skip_if": "a9q5-UsedItem"},
    {"verb": "goto", "area": "2_9_4", "via": "transition", "skip_if": "a9q5-KilledBoss"},
    # The second stage is not placed with the area: it appears where the first falls (Quests On.SecondForm).
    {"verb": "kill", "target": "Metadata/Monsters/Shakari/Shakari", "area": "2_9_4", "appear_timeout": 15, "skip_if": "a9q5-KilledBoss"},
    {"verb": "kill", "target": "Shakari/Shakari2", "area": "2_9_4", "appear_timeout": 15, "expect_flag": "a9q5-KilledBoss", "skip_if": "a9q5-KilledBoss"},
    {"verb": "talk", "npc": "SinShakari", "option": "Shakari", "area": "2_9_4", "expect_flag": "SinShakariReward", "skip_if": "SinShakariReward", "optional": True},
    {"verb": "pantheon", "optional": True},
    {"verb": "talk", "npc": "Irasha", "option": "Shakari", "area": "2_9_town", "expect_flag": "IrashaShakariReward", "skip_if": ["IrashaShakariReward", "a9q5-HaveRewardBook"]},
    {"verb": "reward", "npc": "Irasha", "option": "Shakari Reward", "area": "2_9_town", "skip_if": "a9q5-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act9/Book-a9q5", "expect_flag": "a9q5-UsedRewardBook"},
    {"verb": "passives"},
]
