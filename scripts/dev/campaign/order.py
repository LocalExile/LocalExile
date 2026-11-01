"""The campaign's quest order: Quest.json Ids (data/tables/Quest.json), acts 1-10 and the epilogue.

The usual levelling route through each act, with side quests where their area is passed. Quest files are
`quests/<Id>.py`; a quest without one is reported "not implemented". Not in the order: the Labyrinth trials
(labyrinth_a1..a10), league/mechanic tutorials (e.g. DeepwaterFairgravesAllflameQuest, hideout1, bestiary*,
delve*, betrayal, incursion), bandit_respec and the d2c*/kirac/jun missions.
"""
from pathlib import Path

QUESTS = Path(__file__).resolve().with_name("quests")

ORDER = [
    # Act 1 (Lioneye's Watch, 1_1_town)
    "a1q1",     # Enemy at the Gate (Hillock, The Twilight Strand)
    "a1q5",     # Mercy Mission (Medicine Chest, The Tidal Island; Nessa)
    "a1q4",     # Breaking Some Eggs (glyphs in The Mud Flats)
    "a1q8",     # A Dirty Job (The Fetid Pool; Tarkleigh), side
    "a1q7",     # The Dweller of the Deep (The Flooded Depths; Tarkleigh), side
    "a1q2",     # The Caged Brute (Brutus, The Upper Prison)
    "a1q6",     # The Marooned Mariner (Fairgraves, The Ship Graveyard; Allflame), side
    "a1q3",     # The Siren's Cadence (Merveil, The Cavern of Wrath)
    "a1q9",     # The Way Forward (path to the forest; completed on reaching Act 2)
    # Act 2 (The Forest Encampment, 1_2_town)
    "a2q11",    # The Way Forward (find the encampment)
    "a2q10",    # The Great White Beast (The Den, off The Old Fields), side
    "a2q6",     # Intruders in Black (Fidelitas, The Chamber of Sins; Helena)
    "a2q1",     # The Bandit Lord Kraityn (The Broken Bridge)
    "a2q5",     # Through Sacred Ground (Golden Hand, The Crypt; Yeena), side
    "a2q2",     # The Bandit Lord Alira (The Western Forest)
    "a2q4",     # Sharp and Cruel (Maligaro's Spike, The Weaver's Chambers; Silk)
    "a2q3",     # The Bandit Lord Oak (The Wetlands)
    "a2q7",     # Deal with the Bandits (Eramir; bandit choice)
    "a2q9",     # The Root of the Problem (Baleful Gem, tree to The Vaal Ruins)
    "a2q8",     # Shadow of the Vaal (Vaal Oversoul, The Ancient Pyramid)
    # Act 3 (The Sarn Encampment, 1_3_town)
    "a2q8a3",   # Shadow of the Vaal (Act 3 completion entry)
    "a3q1",     # Lost in Love (Clarissa, The City of Sarn; Tolman's bracelet, The Crematorium; Maramoa)
    "a3q2",     # Lost in Love (Clarissa's Sewer Keys open The Sewers)
    "a3q11",    # Victario's Secrets (busts in The Sewers; Hargan), side
    "a3q4",     # The Ribbon Spool (The Battlefront; Dialla)
    "a3q3",     # The Gemling Queen (Lady Dialla, The Solaris Temple)
    "a3q5",     # Fiery Dust (Thaumetic Sulphite, The Docks; Undying Blockage)
    "a3q13",    # A Swig of Hope (Chitus' Plum / Decanter Spiritus, The Docks), side
    "a3q8",     # Sever the Right Hand (Gravicius, The Ebony Barracks; Maramoa)
    "a3q9",     # Piety's Pets (Piety, The Lunaris Temple; Grigor)
    "a3q12",    # A Fixture of Fate (Golden Pages, The Archives; Siosa), side
    "a3q10",    # The Sceptre of God (Dominus, The Upper Sceptre of God)
    # Act 4 (Highgate, 1_4_town)
    "a4q2",     # Breaking the Seal (Voll, The Dried Lake; Deshret's Banner)
    "a4q6",     # An Indomitable Spirit (Deshret's spirit, The Mines; Tasuni), side
    "a4q3",     # The King of Fury (Kaom, Kaom's Stronghold)
    "a4q4",     # The King of Desire (Daresso, The Grand Arena)
    "a4q5",     # Corpus Malachus (Malachai's organs, The Harvest; Piety)
    "a4q1",     # The Eternal Nightmare (Malachai, The Black Core)
    "a5q1a",    # Return to Oriath (The Ascent, Act 4 part)
    # Act 5 (Overseer's Tower, 1_5_town)
    "a5q1b",    # Return to Oriath (escape The Slave Pens; Overseer Krow)
    "a5q3",     # In Service to Science (Miasmeter, The Control Blocks; Vilenta), side
    "a5q2",     # The Key to Freedom (Justicar Casticus, The Control Blocks)
    "a5q4",     # Death to Purity (High Templar Avarius, The Chamber of Innocence)
    "a5q5",     # The King's Feast (Utula, The Ruined Square)
    "a5q7",     # Kitava's Torments (The Reliquary; Lani), side
    "a5q6",     # The Ravenous God (Kitava, The Cathedral Rooftop)
    # Act 6 (Lioneye's Watch, 2_6_town)
    "a6q4",     # Fallen from Grace (clear The Twilight Strand; Lilly), side
    "a6q5",     # Bestel's Epic (Manuscript, The Tidal Island), side
    "a6q3",     # The Father of War (Tukohama, The Karui Fortress)
    "a6q2",     # Essence of Umbra (Shavronne, The Prison)
    "a6q7",     # The Cloven One (Abberath, The Prisoner's Gate), side
    "a6q6",     # The Puppet Mistress (Ryslatha, The Wetlands), side
    "a6q1",     # The Brine King (The Brine King's Reef)
    # Act 7 (The Bridge Encampment, 2_7_town)
    "a7q5",     # The Silver Locket (The Broken Bridge; Weylam), side
    "a7q2",     # Essence of the Artist (Maligaro's Map, The Crypt; Maligaro, Maligaro's Sanctum; Silk appears with the map)
    "a7q3",     # Web of Secrets (Black Death's venom for Silk's Obsidian Key, the passage into The Den)
    "a7q1",     # The Master of a Million Faces (Greust/Ralakesh, The Ashen Fields)
    "a7q8",     # In Memory of Greust (Azmeri shrine, The Northern Forest), side
    "a7q9",     # Queen of Despair (Gruthkul, The Dread Thicket), side
    "a7q7",     # Lighting the Way (Fireflies, The Dread Thicket; Yeena)
    "a7q6",     # Kishara's Star (The Causeway; Weylam), side
    "a7q4",     # The Mother of Spiders (Arakaali, The Temple of Decay)
    # Act 8 (The Sarn Encampment, 2_8_town)
    "a8q1",     # Essence of the Hag (Doedre, Doedre's Cesspool)
    "a8q5",     # The Wings of Vastiri (Hector Titucius, The Bath House; Hargan), side
    "a8q4",     # Reflection of Terror (Yugul, The High Gardens), side
    "a8q6",     # Love is Dead (Ankh of Eternity, The Quay; Clarissa), side
    "a8q7",     # The Gemling Legion (The Grain Gate; Maramoa), side
    "a8q3",     # Solar Eclipse with Lunar Eclipse (both orbs; Solaris and Lunaris, The Harbour Bridge)
    "a8q2",     # Lunar Eclipse (ends entering The Blood Aqueduct, Act 9)
    # Act 9 (Highgate, 2_9_town)
    "a9q3",     # The Storm Blade (The Vastiri Desert; Petarus and Vanja), side
    "a9q5",     # Queen of the Sands (Shakari, The Oasis), side
    "a9q4",     # Fastis Fortuna (Calendar of Fortune, The Foothills), side
    "a9q2",     # The Ruler of Highgate (Kira/Garukhan, The Quarry), side
    "a9q1",     # Recurring Nightmare (Depraved Trinity, The Rotting Core)
    # Act 10 (Oriath Docks, 2_10_town)
    "a10q1",    # Safe Passage (Bannon, The Cathedral Rooftop)
    "a10q6",    # Vilenta's Vengeance (Vilenta, The Control Blocks; Lani), side
    "a10q4",    # No Love for Old Ghosts (Elixir of Allure, The Ossuary; Weylam), side
    "a10q5",    # Map to Tsoatha (Teardrop, The Reliquary; Lilly), side
    "a10q2",    # Death and Rebirth (Staff of Avarius, The Desecrated Chambers)
    "a10q3",    # An End to Hunger (Kitava, The Feeding Trough)
    # Epilogue (Karui Shores, 2_11_endgame_town)
    "A11q0",    # Toward the Future (Lilly Roth sails to Karui Shores)
    "a10q3a11", # An End to Hunger (Act 11 entry: Lani's reward in Karui Shores when not taken in Oriath Docks)
]


def quest_path(quest, quests_dir=QUESTS):
    return Path(quests_dir) / f"{quest}.py"


def implemented(quests_dir=QUESTS):
    """The quest Ids that have a step file, in ORDER."""
    return [q for q in ORDER if quest_path(q, quests_dir).is_file()]
