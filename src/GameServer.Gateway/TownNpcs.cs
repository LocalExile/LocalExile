/// <summary>
/// Where the campaign towns' NPCs stand (Agents/docs/protocol/17-npcs.md §2). Each town has a fixed room (.arm) that
/// lists its NPCs next to its doodads: <c>x y 0 angle qx qy qz qw a b flag [z] scale "model.ao" "Metadata/NPC/…"</c>
/// (version 36; version 31 rooms have no 0 before the angle), in room cells, the angle in radians (the quaternion is the
/// same turn about z), z only when the flag is 1. The client places the
/// room's own doodads from the same lines through the town's placement (the .dgr node's rotation and the room's origin):
/// its walk-map capture of every town has every plain doodad of the room at exactly <see cref="Room.Cell"/> and
/// <see cref="Room.Angle"/> of its line. The server objects of those lines (the NPCs) are the server's to create, so they
/// go through the same placement. The rows below copy the .arm lines; nothing is measured by eye.
/// <para>Which NPCs: the campaign NPCs of the room (Metadata/NPC/Act*, Epilogue). Not placed: league NPCs (Einhar, Niko,
/// Jun, Alva, the Expedition four, Divinia, Faustus, Kurai, Cassia, Tane, Johan), the Tutorial NPC and Letters of
/// Exile, quest-stage variants (only the first one is placed: Lady Dialla before she withers, Bannon before Innocence
/// leaves him, Lilly by her ship in act 6) and NPCs the Gateway cannot serialize yet (Kirac, the Karui Shores'
/// HelenaIntro: ControlZone).</para>
/// <para>Several NPCs stand on cells the client's walk map marks blocked (on stalls, platforms and piers: Hargan in
/// both Sarn Encampments, Grigor, Highgate's Petarus and Vanja and Tasuni in both acts, Act 6's Sin and Lilly, Act 7's
/// and Act 8's Sin) or on edge cells (Tarkleigh, Eramir, Silk, Helena, Irasha …). They are placed as the room has them;
/// <see cref="NpcCatalog.Placement.FromRoom"/> keeps the Gateway from warning about them.</para>
/// </summary>
static class TownNpcs
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
        // The client's 3-byte rows per placement code (0x142fa04b0; GameServer.Data.Terrain.TilePlacement).
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
    /// a Transitionable state other than the type's.</summary>
    public sealed record Line(string Id, int X, int Y, float Angle, string? Model = null, float? Z = null, byte? State = null);

    /// <summary>A room of a town and its NPC lines.</summary>
    public sealed record Part(Room Room, Line[] Npcs)
    {
        public IEnumerable<NpcCatalog.Placement> Placements() => Npcs.Select(n =>
        {
            var (x, y) = Room.Cell(n.X, n.Y);
            return new NpcCatalog.Placement(n.Id, x, y, Room.Angle(n.Angle), n.Model, n.Z, n.State) { FromRoom = true };
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
        // town.dgr: R270. Also DiallaTownWithered at (250, 553), her later stage.
        new(0xb63e, "1_4_town", new("Metadata/Terrain/Act4/Town/Rooms/town.arm", 759, 943, 3),
        [
            new(Npc + "Act4/Kira", 252, 160, 2.82952f, Z: -335.938f),
            new(Npc + "Act4/Oyun", 260, 566, -0.469922f, Z: -695.313f),
            new(Npc + "Act4/PetarusVanja", 206, 213, 0.520564f, Z: -335.938f),
            new(Npc + "Act4/DiallaTown", 253, 555, 1.11073f, Z: -695.313f),
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
        // (Act 1's Tarkleigh, Bestel). Lilly (type without a model) also sits at (148, 343) with LillySitting.ao; placed
        // here by her ship (Metadata/QuestObjects/Act6/LillyShip at (404, 228)).
        new(0xb0fb, "2_6_town", new("Metadata/Terrain/Act6/BeachTown/Rooms/part2_beach_town.arm", 552, 690, 3),
        [
            new(Npc + "Act6/Tarkleigh", 230, 390, 0f),
            new(Npc + "Act6/Bestel", 166, 360, 0.0872663f),
            new(Npc + "Act6/SinTown", 301, 263, -0.0170866f, Z: -332.881f),
            new(Npc + "Act6/Lilly", 408, 229, -1.57466f, Model: "Metadata/NPC/Act6/Lilly.ao", Z: -83.5598f),
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
        // city_town.dgr (Act8): I. Tolman at (179, 373) has no NPCTalk rows; not placed.
        new(0x0334, "2_8_town", new("Metadata/Terrain/Act8/Town/Rooms/city_town.arm", 759, 667, 0, 69, 69, Seed: 1),
        [
            new(Npc + "Act8/Hargan", 304, 402, -0.492313f),
            new(Npc + "Act8/Maramoa", 304, 349, -1.36175f),
            new(Npc + "Act8/ClarissaTown", 170, 382, -0.488777f),
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
            new(Npc + "Act10/Weylam", 457, 356, -1.08016f, Model: "Metadata/NPC/Act6/RotTooth.ao"),
            new(Npc + "Act10/Lani", 431, 274, 2.71676f),
            new(Npc + "Act10/Lilly", 448, 364, -0.0830655f, Model: "Metadata/NPC/Act5/Lilly.ao"),
        ]),
        // epilogue.dgr: the nodes "fountain" and "entrance_04" (both I) among generated nodes; the client placed the rooms
        // at (299, 299) and (161, 851) (seed 1). Commander Kirac (332, 380) needs ControlZone.
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
        // island_camp.dgr: I. Commander Kirac (786, 675) and HelenaIntro (510, 173: the trigger by the tunnel that plays
        // "HelenaThroughTheTunnel", then hides) need ControlZone.
        new(0xb4e7, "2_11_endgame_town", new("Metadata/Terrain/Act11/Camp/Rooms/camp.arm", 1679, 1058, 0, 92, 92, Seed: 1),
        [
            new(Npc + "Epilogue/Lilly", 186, 366, 0.499106f, Model: "Metadata/NPC/Act5/Lilly.ao", Z: -202.5f),
            new(Npc + "Epilogue/Weylam", 701, 784, -0.879269f, Model: "Metadata/NPC/Act6/RotTooth.ao"),
            new(Npc + "Epilogue/Lani", 712, 778, -0.355253f),
            // Her .ot's create_untransitioned is DisableRendering(): Transitionable state 1 left her invisible live, 2 shows her.
            new(Npc + "Epilogue/Helena", 771, 696, 0.564095f, Model: "Metadata/NPC/Missions/Helena.ao", State: 2),
        ]),
    ];

    public static Town? Of(ushort area) => All.FirstOrDefault(t => t.Area == area);

    /// <summary>The model a town room gives an NPC whose type has none or another (for /npc), else null.</summary>
    public static string? ModelOf(string id) =>
        All.SelectMany(t => t.Npcs).FirstOrDefault(n => n.Model is not null && n.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Model;
}
