/// <summary>
/// Where the campaign towns' NPCs stand (Agents/docs/protocol/17-npcs.md §2). Each town has a fixed room (.arm) that
/// lists its NPCs next to its doodads: <c>x y 0 angle qx qy qz qw a b flag [z] scale "model.ao" "Metadata/NPC/…"</c>
/// (version 36; version 31 rooms have no 0 before the angle), in room cells, the angle in radians (the quaternion is the
/// same turn about z), z only when the flag is 1. The client places the
/// room's own doodads from the same lines through the town's placement (the .dgr node's rotation and the room's origin):
/// its walk-map capture of every town has every plain doodad of the room at exactly <see cref="Room.Cell"/> and
/// <see cref="Room.Angle"/> of its line. The server objects of those lines (the NPCs) are the server's to create, so they
/// go through the same placement. The rows below copy the .arm lines; nothing is measured by eye.
/// <para>Which NPCs: the campaign NPCs of the room (Metadata/NPC/Act*, Epilogue) and the Rogue Harbour's. Not placed: other
/// league NPCs (Einhar, Niko, Jun, Alva, the Expedition four, Divinia, Cassia, Tane, Johan), the Tutorial NPC and Letters of
/// Exile, quest-stage variants (only the first one is placed: Lady Dialla before she withers, Lilly by her ship in act 6;
/// a stage a quest's rules pick stands in the list with the others: Oriath Docks' BannonTown and InnocenceTown) and
/// Kirac and the Karui Shores' HelenaIntro (ControlZone types, which NpcObject writes now; not placed yet).</para>
/// <para>Several NPCs stand on cells the client's walk map marks blocked (on stalls, platforms and piers: Hargan in
/// both Sarn Encampments, Grigor, Highgate's Petarus and Vanja and Tasuni in both acts, Act 6's Sin and Lilly, Act 7's
/// and Act 8's Sin) or on edge cells (Tarkleigh, Eramir, Silk, Helena, Irasha …). They are placed as the room has them;
/// <see cref="NpcCatalog.Placement.FromRoom"/> keeps the Gateway from warning about them.</para>
/// </summary>
public static class TownNpcs
{
    /// <summary>A town room as the client places it: its size in cells (the .arm's own key line, tiles × 23), the
    /// placement code of the .dgr node's rotation (I = 0, R270 = 3, as <c>TilePlacement.FromGraphRotation</c>) and the
    /// grid cell of the room's placed origin. Code and origin are the client's own, from its walk-map capture of each town
    /// (seed 1, <c>&lt;area&gt;_seed1.rooms.json</c>, 2026-10-25): several towns sit inside a border (The Forest
    /// Encampment at (0, 92), The Sarn Encampment at (69, 69) …), and every plain doodad of each room was captured at
    /// exactly its line's placed cell. <paramref name="Seed"/>: the seed of the capture an origin other than (0, 0) comes
    /// from; with another seed the room's NPCs are not placed (Oriath is generated around its fixed rooms).</summary>
    public sealed record Room(string Arm, int Width, int Height, int Code, int OffsetX = 0, int OffsetY = 0, uint? Seed = null)
    {
        // The client's 3-byte rows per placement code (0x142faa4d8; GameServer.Data.Terrain.TilePlacement).
        static readonly byte[] Table = [0, 1, 1, 1, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 1, 1, 1];

        /// <summary>A room cell on the area grid: a = [w − x − 1, x, h − y − 1, y]; x' = a[T1 + 2·T0],
        /// y' = a[(T0 == 0 ? 2 : 0) + T2] (R270: (y, w − 1 − x)).</summary>
        public (int X, int Y) Cell(int x, int y)
        {
            int t0 = Table[Code * 3], t1 = Table[Code * 3 + 1], t2 = Table[Code * 3 + 2];
            Span<int> a = [Width - x - 1, x, Height - y - 1, y];
            return (OffsetX + a[t1 + 2 * t0], OffsetY + a[(t0 == 0 ? 2 : 0) + t2]);
        }

        /// <summary>A room angle on the grid: mirrored for codes 4..7, plus a quarter turn per rotation step, in [0, 2π).</summary>
        public float Angle(float angle)
        {
            const float TwoPi = MathF.PI * 2;
            if (Code >= 4) angle = TwoPi - angle;
            angle = (angle + (Code & 3) * (MathF.PI / 2)) % TwoPi;
            return angle < 0 ? angle + TwoPi : angle;
        }
    }

    /// <summary>An NPC line of the room: NPCs.dat Id, room cell, angle; the line's model when it is not the type's own
    /// (types without a model, e.g. Irasha's <c>Metadata/NPC/NPC</c>, or another pose); its z when the line has one;
    /// a Transitionable state other than the type's; optional quest flags that gate a town stage/pose.</summary>
    public sealed record Line(string Id, int X, int Y, float Angle, string? Model = null, float? Z = null, byte? State = null,
        string? ShownBy = null, string? GoneWith = null);

    /// <summary>A room of a town and its NPC lines.</summary>
    public sealed record Part(Room Room, Line[] Npcs)
    {
        public IEnumerable<NpcCatalog.Placement> Placements() => Npcs.Select(n =>
        {
            var (x, y) = Room.Cell(n.X, n.Y);
            return new NpcCatalog.Placement(n.Id, x, y, Room.Angle(n.Angle), n.Model, n.Z, n.State, n.ShownBy, n.GoneWith) { FromRoom = true };
        });
    }

    public sealed record Town(ushort Area, string AreaId, Part[] Parts)
    {
        public Town(ushort area, string areaId, Room room, Line[] npcs) : this(area, areaId, [new Part(room, npcs)]) { }

        public IEnumerable<Line> Npcs => Parts.SelectMany(p => p.Npcs);

        /// <summary>The town's NPCs for an instance of <paramref name="seed"/>: a room whose origin was captured with
        /// another seed is left out, with a note.</summary>
        public List<NpcCatalog.Placement> Placements(uint seed, List<string>? notes = null)
        {
            var list = new List<NpcCatalog.Placement>();
            foreach (var part in Parts)
            {
                if (part.Room.Seed is uint known && known != seed)
                {
                    notes?.Add($"{AreaId}: {part.Room.Arm} was placed with seed {known}, not {seed}; its {part.Npcs.Length} NPC(s) are not placed");
                    continue;
                }
                list.AddRange(part.Placements());
            }
            return list;
        }
    }

    const string Npc = "Metadata/NPC/";

    public static readonly Town[] All =
    [
        // beachtown.dgr: R270. Lioneye's Watch's cells measured on the real game's minimap before (388, 182), (346, 271),
        // (361, 246) are within one cell of these.
        new(0x235c, "1_1_town", new("Metadata/Terrain/Act1/Town/Rooms/beach_town_unique_normal.arm", 414, 690, 3),
        [
            new(Npc + "Act1/Tarkleigh", 230, 389, -0.155698f),
            new(Npc + "Act1/Nessa", 143, 346, 0.168087f, Z: -231.997f),
            new(Npc + "Act1/Bestel", 166, 362, 0.0872663f),
        ]),
        // forest_town.dgr: R270.
        new(0x2233, "1_2_town", new("Metadata/Terrain/Act2/Town/Rooms/forest_town.arm", 391, 483, 3, 0, 92, Seed: 1),
        [
            new(Npc + "Act2/Yeena", 144, 153, 2.44345f),
            new(Npc + "Act2/Eramir", 204, 159, 2.04085f, Z: -15.625f),
            new(Npc + "Act2/Greust", 214, 203, 1.19238f),
            new(Npc + "Act2/Silk", 144, 179, 0.872664f),
            new(Npc + "Act2/HelenaTown", 134, 168, 1.309f),
        ]),
        // city_town.dgr: I.
        new(0x0431, "1_3_town", new("Metadata/Terrain/Act3/Town/Rooms/city_town.arm", 759, 667, 0, 69, 69, Seed: 1),
        [
            new(Npc + "Act3/Hargan", 303, 402, -0.492313f, Z: -18.3426f),
            new(Npc + "Act3/Grigor", 149, 433, -0.360166f, Z: 54.6875f),
            new(Npc + "Act3/Maramoa", 305, 350, -1.36175f, Z: -62.5f),
            new(Npc + "Act3/ClarissaTown", 179, 376, -0.985034f, Z: 54.6875f),
        ]),
        // town.dgr: R270. Both Diallas: DiallaTown, and DiallaTownWithered, her stage once the Rapture Device works
        // (Acts/Act4.cs: a4q1-RaptureActivated decides which one stands there).
        new(0xb63e, "1_4_town", new("Metadata/Terrain/Act4/Town/Rooms/town.arm", 759, 943, 3),
        [
            new(Npc + "Act4/Kira", 252, 160, 2.82952f, Z: -335.938f),
            new(Npc + "Act4/Oyun", 260, 566, -0.469922f, Z: -695.313f),
            new(Npc + "Act4/PetarusVanja", 206, 213, 0.520564f, Z: -335.938f),
            new(Npc + "Act4/DiallaTown", 253, 555, 1.11073f, Z: -695.313f),
            new(Npc + "Act4/DiallaTownWithered", 250, 553, 0.399797f, Model: "Metadata/NPC/Act4/DiallaWithered.ao"),
            new(Npc + "Act4/Tasuni", 311, 407, 0.00824316f, Z: -392.663f),
        ]),
        // 1_5_town.dgr: I.
        new(0xbeab, "1_5_town", new("Metadata/Terrain/Act5/Town/Rooms/town_01.arm", 437, 437, 0, 138, 138, Seed: 1),
        [
            new(Npc + "Act5/Utula", 214, 180, -0.144973f),
            new(Npc + "Act5/BannonTown", 281, 210, -1.0472f, Z: -653.872f),
            new(Npc + "Act5/Lani", 232, 172, -0.785399f),
            new(Npc + "Act5/Vilenta", 275, 178, -1.5708f),
        ]),
        // p2_beachtown.dgr: R270. Act 6's NPCs.dat Ids have no .ot of their own: the client files them on their Metadata
        // (Act 1's Tarkleigh, Bestel). Lilly stands by her ship (Metadata/QuestObjects/Act6/LillyShip at (404, 228))
        // until Tukohama falls (a6q3-KilledBoss), then sits on the dock at (148, 343) with LillySitting.ao.
        new(0xb0fb, "2_6_town", new("Metadata/Terrain/Act6/BeachTown/Rooms/part2_beach_town.arm", 552, 690, 3),
        [
            new(Npc + "Act6/Tarkleigh", 230, 390, 0f),
            new(Npc + "Act6/Bestel", 166, 360, 0.0872663f),
            new(Npc + "Act6/SinTown", 301, 263, -0.0170866f, Z: -332.881f),
            new(Npc + "Act6/Lilly", 408, 229, -1.57466f, Model: "Metadata/NPC/Act6/Lilly.ao", Z: -83.5598f, GoneWith: "a6q3-KilledBoss"),
            new(Npc + "Act6/Lilly", 148, 343, 1.05179f, Model: "Metadata/NPC/Act6/LillySitting.ao", Z: -237.432f, ShownBy: "a6q3-KilledBoss"),
        ]),
        // bridge_town.tgr: node "town" I (its other nodes are flow sources and sinks without rooms).
        new(0x2a6d, "2_7_town", new("Metadata/Terrain/Act7/Town/Rooms/broken_bridge_town.arm", 230, 759, 0, 437, 207, Seed: 1),
        [
            new(Npc + "Act7/Eramir", 136, 365, -2.25908f),
            new(Npc + "Act7/YeenaTown", 129, 425, -0.387718f, Z: -429.688f),
            new(Npc + "Act7/Helena", 149, 419, 0.261799f, Z: -432.065f),
            new(Npc + "Act7/SinTown", 143, 202, -2.18261f, Z: -433.084f),
            new(Npc + "Act7/Weylam", 95, 203, 1.00597f, Model: "Metadata/NPC/Act6/RotTooth.ao"),
        ]),
        // city_town.dgr (Act8): I. Cosmetic Tolman stands beside Clarissa before a8q6-KilledBoss.
        new(0x0334, "2_8_town", new("Metadata/Terrain/Act8/Town/Rooms/city_town.arm", 759, 667, 0, 69, 69, Seed: 1),
        [
            new(Npc + "Act8/Hargan", 304, 402, -0.492313f),
            new(Npc + "Act8/Maramoa", 304, 349, -1.36175f),
            new(Npc + "Act8/ClarissaTown", 170, 382, -0.488777f),
            new(Npc + "Act8/Tolman", 179, 373, -1.17812f, Model: "Metadata/NPC/Act8/Tolman.ao", GoneWith: "a8q6-KilledBoss"),
            new(Npc + "Act8/SinTown", 205, 306, 1.64849f),
        ]),
        // town.dgr (Act9): R270.
        new(0x23f3, "2_9_town", new("Metadata/Terrain/Act9/Town/Rooms/town.arm", 759, 943, 3),
        [
            new(Npc + "Act9/PetarusVanja", 231, 160, 2.61603f, Z: -335.938f),
            new(Npc + "Act9/Irasha", 200, 182, 2.04771f, Model: "Metadata/NPC/Act9/Irasha/Irasha.ao", Z: -335.938f),
            new(Npc + "Act9/Tasuni", 311, 407, 0.00824316f, Z: -392.663f),
            new(Npc + "Act9/SinTown", 297, 184, -0.540068f, Z: -319.633f),
        ]),
        // ship_town.dgr: I. InnocenceTown stands in Bannon's cell (470, 295) for the later stage.
        new(0x95b0, "2_10_town", new("Metadata/Terrain/Act10/Town/Rooms/dock_town.arm", 851, 851, 0),
        [
            new(Npc + "Act10/SinTown", 490, 275, -2.79379f, Z: -86.9565f),
            new(Npc + "Act10/BannonTown", 470, 295, -0.497837f),
            // dock_town.arm lists InnocenceTown on Bannon's cell: Bannon's later stage once he takes Avarius' staff
            // (Acts/Act10.cs: a10q2-BannonTransformed decides which one stands there).
            new(Npc + "Act10/InnocenceTown", 470, 295, -0.497837f),
            new(Npc + "Act10/Weylam", 457, 356, -1.08016f, Model: "Metadata/NPC/Act6/RotTooth.ao"),
            new(Npc + "Act10/Lani", 431, 274, 2.71676f),
            new(Npc + "Act10/Lilly", 448, 364, -0.0830655f, Model: "Metadata/NPC/Act5/Lilly.ao"),
        ]),
        // epilogue.dgr: the nodes "fountain" and "entrance_04" (both I) among generated nodes; the client placed the rooms
        // at (299, 299) and (161, 851) (seed 1). Commander Kirac (332, 380) is not placed yet.
        new(0xf580, "2_11_town",
        [
            new(new("Metadata/Terrain/Act11/Town/Rooms/Unique/fountain.arm", 575, 575, 0, 299, 299, Seed: 1),
            [
                new(Npc + "Epilogue/Zana", 327, 417, -1.0472f),
                new(Npc + "Epilogue/Innocence", 231, 453, 0.888412f),
                new(Npc + "Epilogue/Sin", 252, 457, -0.832619f),
                new(Npc + "Epilogue/Weylam", 344, 359, -1.83259f, Model: "Metadata/NPC/Act6/RotTooth.ao"),
                new(Npc + "Epilogue/Lilly", 339, 369, -0.523599f, Model: "Metadata/NPC/Act5/Lilly.ao"),
                new(Npc + "Epilogue/Lani", 333, 343, -2.09439f),
            ]),
            // A version 31 room: its lines have no 0 before the angle. Helena as in the Karui Shores (state 2).
            new(new("Metadata/Terrain/Act11/Town/Rooms/Unique/entrance_04.arm", 851, 575, 0, 161, 851, Seed: 1),
            [
                new(Npc + "Epilogue/Helena", 389, 175, 0.742503f, Model: "Metadata/NPC/Missions/Helena.ao", State: 2),
            ]),
        ]),
        // island_camp.dgr: I. Commander Kirac (786, 675) and HelenaIntro (510, 173: tunnel intro trigger for a11q1).
        new(0xb4e7, "2_11_endgame_town", new("Metadata/Terrain/Act11/Camp/Rooms/camp.arm", 1679, 1058, 0, 92, 92, Seed: 1),
        [
            new(Npc + "Epilogue/Lilly", 186, 366, 0.499106f, Model: "Metadata/NPC/Act5/Lilly.ao", Z: -202.5f),
            new(Npc + "Epilogue/Weylam", 701, 784, -0.879269f, Model: "Metadata/NPC/Act6/RotTooth.ao"),
            new(Npc + "Epilogue/Lani", 712, 778, -0.355253f),
            // Her .ot's create_untransitioned is DisableRendering(): Transitionable state 1 left her invisible live, 2 shows her.
            new(Npc + "Epilogue/Helena", 771, 696, 0.564095f, Model: "Metadata/NPC/Missions/Helena.ao", State: 2),
            new(Npc + "Epilogue/HelenaIntro", 510, 173, 0.903148f, Model: "Metadata/NPC/Missions/Helena.ao", State: 2),
            new(Npc + "Epilogue/Kirac", 786, 675, -2.32129f, Model: "Metadata/Monsters/Masters/Kirac.ao"),
        ]),
        // The Rogue Harbour (HeistHub). Not a campaign town, but its hub room names its NPCs the same way. hub.dgr places
        // the single node "hub" with rotation I, at (92, 92) on every seed; a version 31 room (no 0 before the angle),
        // 19 x 19 tiles = 437 x 437 cells. The nine rogues around the planning table in ops_1.arm are
        // Metadata/NPC/League/Heist/PlanningNPC, which NPCs.dat has no row for, so they are not placed.
        // The official server's creates in the Rogue Harbour stand at exactly these cells and angles (8 of the 13 NPCs
        // are in the creates it sends; the rest were out of range).
        new(StashCatalog.HeistHub, "HeistHub", new("Metadata/Terrain/Leagues/Heist/Hub/Rooms/hub_1.arm", 437, 437, 0, 92, 92),
        [
            new(Npc + "League/Heist/TheBoss", 228, 264, -0.694438f),         // Kurai, the Administrator
            new(Npc + "League/Heist/Fence", 89, 290, 0.26262f),              // Faustus, the Fence
            new(Npc + "League/Heist/Driver", 126, 292, 1.49717f),            // Adiyah, the Wayfinder
            new(Npc + "League/Heist/Barber", 90, 302, 0.234507f),            // Whakano, the Barber
            new(Npc + "League/Heist/Lockpick1", 130, 405, -0.835378f),       // Karst, the Lockpick
            new(Npc + "League/Heist/Interrogator1", 389, 323, -0.79303f),    // Niles, the Interrogator
            new(Npc + "League/Heist/Soldier1", 292, 313, -0.296804f),        // Huck, the Soldier
            new(Npc + "League/Heist/HeavyLifter1", 294, 298, -1.42205f),     // Tibbs, the Giant
            new(Npc + "League/Heist/Detector1", 165, 350, -0.636915f),       // Nenet, the Scout
            new(Npc + "League/Heist/Demolitionist1", 326, 395, -0.700871f),  // Vinderi, the Dismantler
            new(Npc + "League/Heist/CatBurglar1", 315, 257, -0.26125f),      // Tullina, the Catburglar
            new(Npc + "League/Heist/Illusionist1", 231, 370, -0.25517f),     // Gianna, the Master of Disguise
            new(Npc + "League/Heist/Engineer1", 145, 359, -0.0840756f),      // Isla, the Engineer
        ]),
        // Kingsmarch (KalguuranSettlersLeague). Its hub room names its NPCs and both its stashes the same way the Rogue
        // Harbour's does, so they are placed from its lines instead of the ring of guesses NpcCatalog.Towns held.
        // settlers_hub.dgr places the single node "kingsmarch" with rotation I; the Gateway's own generation puts the room
        // at (138, 138) with code 0 at every seed tried (1, 7, 39403887), so no Seed is pinned. 27 x 45 tiles = 621 x 1035
        // cells. No capture of ours enters Kingsmarch, so the room file is the only source here; the cells are not checked
        // against the wire.
        // Not placed: Gwennen, the Gambler (VillageGwennen) -- the room has no line for her, so there is no official cell
        // for her at all; the Kalguuran workers, thugs and tavern guests of the same lines are
        // HubTown/Objects/TemporaryNPCWorker and DecoUpgradeNPC*, which NPCs.dat has no row for.
        new(StashCatalog.Kingsmarch, "KalguuranSettlersLeague",
            new("Metadata/Terrain/Leagues/Settlers/HubTown/Rooms/kingsmarch.arm", 621, 1035, 0, 138, 138),
        [
            new(Npc + "League/Kalguur/VillageMayor", 429, 624, -1.5708f),        // Johan, the King's Hand -> (567, 762)
            new(Npc + "League/Kalguur/VillageFaustus", 389, 625, 1.17173f),      // Faustus, the Financier -> (527, 763)
            new(Npc + "League/Kalguur/VillageRecruiter", 422, 664, -0.687523f),  // Raulf, the Recruiter -> (560, 802)
            new(Npc + "League/Kalguur/VillageTujen", 283, 429, -0.7837f),        // Tujen, the Harbourmaster -> (421, 567)
            new(Npc + "League/Kalguur/VillageRog", 316, 523, -1.34867f),         // Rog, the Disenchanter -> (454, 661)
            new(Npc + "League/Kalguur/VillageDannig", 493, 478, -0.415451f),     // Dannig, the Blacksmith -> (631, 616)
            new(Npc + "League/Kalguur/VillageIsla", 213, 547, -2.46387f),        // Isla, the Engineer -> (351, 685)
            // The room gives Sonja the Kalguuran farmer's model; her type's own animated_object is Isla's.
            new(Npc + "League/Kalguur/VillageFarmer", 367, 794, -1.6406f,
                Model: Npc + "League/Kalguur/KalguuranFarmer/KalguuranFarmer.ao"), // Sonja, the Farmer -> (505, 932)
        ]),
        // The Monastery of the Keepers (ChayulaLeague). breach_hub.dgr places the single node "hub" with rotation I; the
        // Gateway's own generation puts hub.arm at (0, 0) with code 0 at every seed tried (1, 7 and the official capture's
        // own 1159934363), so a room cell is an area cell and no Seed is pinned. 30 x 36 tiles = 690 x 828 cells.
        // Measured against an official capture of the Monastery: the three NPC creates there stand at exactly the cell
        // and the angle these lines give, with the angle normalised into [0, 2pi) as Room.Angle does --
        // Ailith (370, 249) 0, The Dreamer (506, 652) 5.497788, The
        // Word of the Dreamer (150, 325) 4.590886 -- and so do both stashes (StashCatalog).
        // Not placed: the acolytes, the child monk and the farmer clone of the same lines are
        // MiscellaneousObjects/Brequel/* and Doodad, which NPCs.dat has no row for.
        new(StashCatalog.ChayulaHub, "ChayulaLeague",
            new("Metadata/Terrain/Leagues/Breach/Hub/Rooms/hub.arm", 690, 828, 0),
        [
            new(Npc + "League/Chayula/ChayulaFarmerHub", 370, 249, -0f),         // Ailith, First of the Keepers
            // Neither of these two has an .ot of its own (client.db object_types has no row), so the room's own model
            // rides the create, as the capture's creates carry it.
            new(Npc + "League/Breach/Chayula", 506, 652, -0.785397f,             // The Dreamer
                Model: "Metadata/Terrain/Leagues/Breach/Doodads/ChayulaHealingTree01.ao"),
            new(Npc + "Info/ChayulaBook", 150, 325, -1.6923f,                    // The Word of the Dreamer
                Model: "Metadata/Terrain/Leagues/Breach/Doodads/BreachBook01.ao", Z: -322f),
        ]),
    ];

    public static Town? Of(ushort area) => All.FirstOrDefault(t => t.Area == area);

    /// <summary>The model a town room gives an NPC whose type has none or another (for /npc), else null.</summary>
    public static string? ModelOf(string id) =>
        All.SelectMany(t => t.Npcs).FirstOrDefault(n => n.Model is not null && n.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Model;
}
