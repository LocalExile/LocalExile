"""a1q4 "Breaking Some Eggs": the three Rhoa Nests in The Mud Flats each drop a glyph; the Glyph Wall takes them and
opens The Submerged Passage; Tarkleigh gives two rewards."""
QUEST = "a1q4"
DONE = "a1q4-QuestRewardTaken"
REQUIRES = ["a1q1"]

PLACED = "a1q4-PlacedGlyphs"

STEPS = [
    {"verb": "goto", "area": "1_1_2", "skip_if": PLACED},
    {"verb": "goto", "area": "1_1_3", "skip_if": PLACED},
    # Each nest drops its own glyph; open the nearest unused nest and take what it drops, three times. Both steps are
    # done at once when every nest is open and the glyphs are carried (a resumed run).
    *[step for _ in range(3) for step in (
        {"verb": "interact", "target": "RhoaChest", "skip_if": PLACED},
        {"verb": "pickup", "item": "Metadata/Items/QuestItems/Glyphs/Glyph", "skip_if": PLACED},
    )],
    {"verb": "interact", "target": "GlyphWall", "expect_flag": PLACED, "skip_if": PLACED},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Glyph Reward", "area": "1_1_town", "skip_if": "a1q4-QuestRewardTaken"},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Glyph Reward 2", "area": "1_1_town", "skip_if": "a1q4-QuestReward2Taken",
     "optional": True},
    {"verb": "passives"},
]
