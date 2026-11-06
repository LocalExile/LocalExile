using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class LabyrinthTrialTests
{
    [Fact]
    public void TrialDoorBlobAndUnblockFollowTheClientReaders()
    {
        const string model = "Metadata/Terrain/Doodads/Labyrinth/LabyrinthDoor.ao";
        byte[] closed = LabyrinthTrials.Blob(LabyrinthTrials.IntroDoor, model, 160, 57, 0, 1, LabyrinthTrials.Closed);
        byte[] open = LabyrinthTrials.Blob(LabyrinthTrials.IntroDoor, model, 160, 57, 0, 1, LabyrinthTrials.Open);

        // Positioned (19: flags1 0x20 no static position, 0x08 blocks no walker with its u8), Animated (1 + u32 count +
        // UTF-16), InteractionAction, Transitionable, TriggerableBlockage, tasks.
        int animated = 2 + 19, after = animated + 1 + 4 + model.Length * 2;
        Assert.Equal(160, BitConverter.ToInt32(closed, 2));
        Assert.Equal(new byte[] { 0, 0x2a, 0, 1, 1 }, closed[16..21]);
        Assert.Equal(1, closed[animated]);
        Assert.Equal(model.Length, BitConverter.ToInt32(closed, animated + 1));
        Assert.Equal(new byte[] { 0, LabyrinthTrials.Closed, 1, 0, 0 }, closed[after..]);
        Assert.Equal(new byte[] { 0, LabyrinthTrials.Open, 0, 0 }, open[after..]);
        Assert.Equal(4, LabyrinthTrials.TransitionableIndex(LabyrinthTrials.IntroDoor));
        Assert.Equal(1, LabyrinthTrials.TransitionableIndex(LabyrinthTrials.Lever));

        // 0x1c6: id, world header, u8 0 (unblocked: no sections follow).
        Assert.Equal(new byte[] { 0x01, 0xc6, 0, 0, 0x30, 0x05, 0, 0, 0, 0, 0, 0, 0 }, InstanceEntities.BlockageUpdate(0x3005, false));

        // The Crematorium's hot plate: Positioned (f0 0x04: its fixed height, 125 below, after flags1; 26: the marker is
        // −z / 7.8125 = −16, as the server sends it), Animated,
        // Transitionable, Buffs (u16 count), tasks. A 1x3 lies three tiles along its angle.
        const string plate = "Metadata/Terrain/Doodads/Traps/hotplate1_3.ao";
        byte[] lava = LabyrinthTrials.Blob(LabyrinthTrials.Lava1x3, plate, 57, 103, 0, 1, LabyrinthTrials.Open, 125);
        Assert.Equal(new byte[] { 0x04, 0x02, 0xf0, 0xff, 0xff, 0xff }, lava[16..22]);
        Assert.Equal(125f, BitConverter.ToSingle(lava, 22));
        Assert.Equal(new byte[] { LabyrinthTrials.Open, 0, 0, 0 }, lava[(2 + 26 + 1 + 4 + plate.Length * 2)..]);
        Assert.Equal(3, LabyrinthTrials.TransitionableIndex(LabyrinthTrials.Lava1x3));
        Assert.True(LabyrinthTrials.OnPlate(LabyrinthTrials.Lava1x3, 57, 103, 0, (90, 110)));
        Assert.False(LabyrinthTrials.OnPlate(LabyrinthTrials.Lava1x3, 57, 103, 0, (57, 120)));
        Assert.True(LabyrinthTrials.OnPlate(LabyrinthTrials.Lava1x3, 57, 103, MathF.PI / 2, (60, 136)));
        // Its levers' gears (crematorium_intro.arm, seed 1's cells): each turns the ones beside it (live 2026-11-01).
        (int, int)[] gears = [(788, 208), (717, 274), (717, 300), (832, 186), (832, 158)], levers = [(826, 171), (711, 286), (781, 181)];
        Assert.Equal([3, 4], LabyrinthTrials.TurnedGears(gears, levers, 0));
        Assert.Equal([1, 2], LabyrinthTrials.TurnedGears(gears, levers, 1));
        Assert.Equal([0], LabyrinthTrials.TurnedGears(gears, levers, 2));
    }

    [Fact]
    public void SpikeTrapsFireAsAWaveAlongEachChain()
    {
        // The Lower Prison's intro room: a ring of 16 traps a tile apart, and a pair two tiles away from it.
        (int, int)[] ring = [(92, 115), (115, 115), (138, 115), (161, 115), (184, 115), (184, 138), (184, 161), (184, 184),
            (184, 207), (161, 207), (138, 207), (115, 207), (92, 207), (92, 184), (92, 161), (92, 138)];
        (int, int)[] traps = [.. ring.Reverse(), (300, 115), (323, 115)];

        // The room's lever stands beside the ring's (184, 161), its Locked Door beside (161, 115).
        var chains = LabyrinthTrials.SpikeChains(traps, [(211, 161)], [(172, 117)]);

        Assert.All(chains.Take(16), c => Assert.Equal(0, c.Chain));
        var wave = Enumerable.Range(0, 16).OrderBy(i => chains[i].Order).Select(i => traps[i]).ToList();
        Assert.Equal(Enumerable.Range(0, 16), chains.Take(16).Select(c => c.Order).Order());
        for (int k = 1; k < 16; k++)   // once round, each a step after its neighbour
            Assert.Equal(23, Math.Abs(wave[k].Item1 - wave[k - 1].Item1) + Math.Abs(wave[k].Item2 - wave[k - 1].Item2));
        // From the lever straight towards the door, then on round.
        Assert.Equal((184, 161), wave[0]);
        Assert.Equal((184, 138), wave[1]);
        Assert.Equal(3, wave.IndexOf((161, 115)));
        Assert.All(chains.Take(16), c => Assert.False(c.Checker));
        // The pair is no ring: a checkerboard, its two traps in turn.
        Assert.Equal([(1, 0, true), (1, 1, true)], chains[16..].Order());
    }

    [Fact]
    public void DartTrapFiresFourDartsAQuarterTurnApartFromItsFacing()
    {
        const string model = "Metadata/Terrain/Labyrinth/Traps/statueturret_circleclean.ao";
        List<(double, double)> Darts(float angle) =>
            [.. LabyrinthTrials.ArrowDirections(angle).Select(d => (Math.Round(d.X, 3) + 0, Math.Round(d.Y, 3) + 0)).Order()];

        // The Imperial Gardens' turrets face 45° or 90°: the first shoot along the diagonals, the others straight.
        Assert.Equal([(-0.707, -0.707), (-0.707, 0.707), (0.707, -0.707), (0.707, 0.707)], Darts(0.785397f));
        Assert.Equal([(-1.0, 0.0), (0.0, -1.0), (0.0, 1.0), (1.0, 0.0)], Darts(1.5708f));

        // Positioned (the room line's angle, the .ot's team 14), Transitionable, then Animated with the room line's model.
        byte[] blob = LabyrinthTrials.ArrowTrapBlob(195, 138, 0.785397f, model);
        Assert.Equal(0.785397f, BitConverter.ToSingle(blob, 10));
        Assert.Equal(14, BitConverter.ToUInt16(blob, 14));
        Assert.Equal(new byte[] { 1, 1 }, blob[20..22]);
        Assert.Equal(model.Length, BitConverter.ToInt32(blob, 22));
    }

    [Fact]
    public void RoombasFollowTheRunOfRailsTheyStandOn()
    {
        // The Catacombs trial's rails and roombas (catacomb_intro.arm, room cells).
        (int, int)[] rails = [(48, 54), (103, 59), (75, 79), (152, 80), (64, 117), (103, 118), (75, 122), (172, 149), (264, 149),
            (172, 218), (264, 218), (119, 278), (181, 278), (119, 340), (181, 340)];
        const float Half = MathF.PI / 2;
        (int, int, float)[] roombas = [(124, 81, -Half), (104, 79, 0), (172, 181, MathF.PI), (264, 181, 0), (151, 278, -Half),
            (121, 309, MathF.PI), (181, 310, 0), (151, 340, Half)];

        var paths = LabyrinthTrials.RoombaPaths(rails, roombas);

        // Where the flying one's run crosses the first ground one's, each keeps the run it faces along: the flying one
        // turns a corner, the ground one runs a crooked U (one run is 5 degrees off), both to and fro, set out as they face.
        void Run(LabyrinthTrials.RoombaPath? path, bool ring, params (int, int)[] rails)
        {
            Assert.Equal(ring, path!.Ring);
            var order = path.Rails.Select(r => (r.X, r.Y)).ToList();
            int first = ring ? order.IndexOf(rails[0]) : 0;
            Assert.Equal(rails, order.Skip(first).Concat(order.Take(first)));
        }
        Run(paths[0], false, (152, 80), (75, 79), (75, 122));
        Run(paths[1], false, (64, 117), (103, 118), (103, 59), (48, 54));
        // The other two runs close a rectangle and a square, their roombas all facing round them the same way.
        Run(paths[2], true, (172, 149), (172, 218), (264, 218), (264, 149));
        Assert.Equal(paths[2]!.Rails.ToHashSet(), paths[3]!.Rails.ToHashSet());
        foreach (var square in paths[4..]) Run(square, true, (181, 278), (119, 278), (119, 340), (181, 340));

        // The Chamber of Sins trial's first track (intro.arm, room cells): two lanes 9 cells apart, a sawblade on each, and
        // each heads for its lane's nearer end first, so the two part.
        (int, int)[] track = [.. new[] { 111, 120 }.SelectMany(y => new[] { 128, 151, 174, 197, 220 }.Select(x => (x, y)))];
        var saws = LabyrinthTrials.RoombaPaths(track, [(205, 111, Half), (142, 120, Half)], [.. track.Select(_ => Half)]);
        Assert.False(saws[0]!.Ring);
        Assert.Equal((220, 111), saws[0]!.TowardNearerEnd((205, 111)).Rails[^1]);
        Assert.Equal((128, 120), saws[1]!.TowardNearerEnd((142, 120)).Rails[^1]);
        // Each lever stops one of every pair (live 2026-11-01, the original): the lever nearer the trial's door the inner
        // lanes, the other the outer ones; the lone sawblades run on.
        (int, int)[] sawblades = [(167, 384), (167, 393), (169, 245), (169, 254), (205, 111), (142, 120), (104, 176), (204, 292), (213, 292)];
        (int, int)[] levers = [(153, 268), (148, 367)];
        Assert.Equal([0, 3, 7], LabyrinthTrials.SwitchedSaws(sawblades, levers, 0, (229, 58)));
        Assert.Equal([1, 2, 8], LabyrinthTrials.SwitchedSaws(sawblades, levers, 1, (229, 58)));

        // The Crypt trial's spinner track (crypt_intro.arm, room cells): the upper room's square, whose middle joins a
        // crossing to a TriCorner rail switch, and a lane with a straight stopper. Its spinners go round the square's
        // small rings until the switch is lifted, then round the square (live 2026-11-01, the original); a stopper turns
        // its spinner back until it is lifted, then it runs to its lane's end and back.
        const string Pieces = "Metadata/Terrain/Doodads/Traps/";
        var spinners = LabyrinthTrials.SpinnerTrack.Build([(204, 96, 0, Pieces + "spinner_rail_corner_v02_01.ao"),
            (204, 73, Half, Pieces + "spinner_rail_crossing_v02_01.ao"), (250, 50, MathF.PI, Pieces + "spinner_rail_corner_v02_01.ao"),
            (250, 96, -Half, Pieces + "spinner_rail_corner_v01_01.ao"), (204, 50, Half, Pieces + "spinner_rail_corner_v01_01.ao"),
            (250, 73, -Half, Pieces + "SpinnerStopper_TriCorner.ao"), (117, 299, MathF.PI, Pieces + "spinner_rail_end_v01_01.ao"),
            (117, 253, MathF.PI, Pieces + "SpinnerStopper_Straight.ao"), (117, 230, MathF.PI, Pieces + "spinner_rail_corner_v01_01.ao"),
            (94, 230, -Half, Pieces + "spinner_rail_end_v01_01.ao")])!;
        List<(int, int)> Goes((int, int) at, float angle, bool lifted, int pieces)
        {
            var ((from, to), turn, seen) = (spinners.Start(at, angle)!.Value, 0, new List<(int, int)>());
            for (int k = 0; k < pieces; k++)
            {
                seen.Add(spinners.Rails[to]);
                (int next, turn) = spinners.Next(from, to, turn, _ => lifted);
                (from, to) = (to, next);
            }
            return seen;
        }
        Assert.Equal([(204, 50), (204, 73), (250, 73), (250, 50), (204, 50)], Goes((212, 50), -Half, false, 5));
        Assert.Equal([(250, 96), (250, 73), (204, 73), (204, 96), (250, 96)], Goes((227, 96), Half, false, 5));
        Assert.Equal([(204, 50), (204, 73), (204, 96), (250, 96), (250, 73), (250, 50)], Goes((212, 50), -Half, true, 6));
        Assert.Equal([(117, 253), (117, 299), (117, 253)], Goes((117, 281), 0, false, 3));
        Assert.Equal([(117, 253), (117, 230), (94, 230), (117, 230)], Goes((117, 281), 0, true, 4));
    }

    [Fact]
    public void TrialsTakeTheirAreaAndCompletionFlag()
    {
        const string json = """
            [{"WorldAreas":1,"__col1":263,"__col7":2,"__col2":151},
             {"WorldAreas":9,"__col1":264,"__col7":2},
             {"WorldAreas":0,"__col1":265,"__col7":7}]
            """;
        var flags = new Dictionary<int, string> { [2] = "NormalLabyrinthCompletedPrison" };

        var trials = LabyrinthTrials.Parse(json, ["1_1_1", "1_1_7_1"], flags);

        Assert.Equal([new LabyrinthTrials.Trial("1_1_7_1", "NormalLabyrinthCompletedPrison", 263)], trials);
    }

    [Fact]
    public void LabyrinthRoomLinksWirePuzzlesAndSpikeChains()
    {
        // ElegantHouse bottleneck_01's links (live 2026-11-01, as the original): cranks 1 and 2 count on handler 5, which
        // opens doors 69 and 68 and spends the cranks; each crank turns its gears (1 → 44 → 13), which stop as it comes back
        // up; an opened door disables the lever outside it (68 → 3, 69 → 4).
        const string P = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/";
        string[] types = [.. Enumerable.Repeat("Metadata/MiscellaneousObjects/Doodad", 70)];
        (types[1], types[2], types[3], types[4], types[5]) = (P + "Switch_Reset_16", P + "Switch_Reset_16", P + "Switch_Once", P + "Switch_Once", P + "Counter_Handler");
        (types[13], types[44], types[68], types[69]) = (P + "TimerGears", P + "TimerGears", P + "Door_Counter", P + "Door_Counter");
        GameServer.Data.Terrain.Generation.PlacedLink L(int from, int to, string name) => new(from, to, name);
        var wiring = new LabyrinthWiring(types, [L(1, 5, "counter"), L(1, 44, "next"), L(2, 5, "counter"), L(3, 68, "next"), L(4, 69, "next"),
            L(5, 69, "transition1"), L(5, 68, "transition2"), L(5, 2, "transition3"), L(5, 1, "transition4"), L(44, 13, "next"),
            L(68, 3, "disable1"), L(69, 4, "disable1")]);

        Assert.Equal([(1, LabyrinthWiring.Change.On), (44, LabyrinthWiring.Change.On), (13, LabyrinthWiring.Change.On)], wiring.Turn(1, true));
        Assert.Equal([(1, LabyrinthWiring.Change.Off), (44, LabyrinthWiring.Change.Off), (13, LabyrinthWiring.Change.Off)], wiring.Turn(1, false));
        wiring.Turn(1, true);
        var opened = wiring.Turn(2, true);
        Assert.Equal([(2, LabyrinthWiring.Change.On), (5, LabyrinthWiring.Change.On), (69, LabyrinthWiring.Change.On), (4, LabyrinthWiring.Change.Disabled),
            (68, LabyrinthWiring.Change.On), (3, LabyrinthWiring.Change.Disabled), (2, LabyrinthWiring.Change.Spent), (1, LabyrinthWiring.Change.Spent)], opened);
        Assert.Empty(wiring.Turn(1, false));
        Assert.Equal(16, LabyrinthWiring.CrankSeconds(P + "Switch_Reset_16"));

        // The Lower Prison trial's spikes: a chain of 16 round its room fires as a wave along its links; two zigzag chains
        // side by side rise as a checkerboard's colours.
        (int, int)[] traps = [(1058, 483), (1058, 460), (1058, 437), (1081, 391), (1012, 621), (989, 644), (989, 621), (989, 667)];
        var spikes = LabyrinthTrials.LinkedSpikes(traps, [(0, 1), (1, 2), (2, 3), (4, 5), (6, 7)], []);
        Assert.Equal([0, 1, 2, 3], spikes.Take(4).Select(s => s.Order));
        Assert.All(spikes.Take(4), s => Assert.False(s.Checker));
        Assert.All(spikes.Skip(4), s => Assert.True(s.Checker));
        Assert.Equal([0, 0, 1, 1], spikes.Skip(4).Select(s => s.Order));
    }

    [Fact]
    public void LabyrinthRoombasFollowTheirLinkedRails()
    {
        // OutdoorHouse indoor_trap_var2_2 (live 2026-11-01, the original): the ground roomba comes in from its reset rail 1
        // to a ring of three, the flying one from rail 2 round the same three the other way.
        (int, int)[] rails = [(118, 114), (86, 60), (73, 60), (76, 114), (118, 72), (0, 0), (0, 0), (0, 0), (118, 72), (118, 114), (76, 114), (58, 76), (68, 86)];
        (int, int, bool)[] links = [(0, 4, false), (1, 12, false), (2, 11, false), (3, 0, false), (4, 3, false), (8, 9, false), (9, 10, false),
            (10, 8, false), (11, 8, false), (12, 3, false)];
        var ground = LabyrinthTrials.LinkedPath(rails, links, 1);
        Assert.Equal([(86, 60), (68, 86), (76, 114), (118, 114), (118, 72)], ground.Rails);
        Assert.True(ground.Ring && ground.FromFirst);
        Assert.Equal(2, ground.Loop);
        var flying = LabyrinthTrials.LinkedPath(rails, links, 2);
        Assert.Equal([(73, 60), (58, 76), (118, 72), (118, 114), (76, 114)], flying.Rails);

        // 2x2_trap_05: a loop whose third rail teleports back to its start.
        (int, int)[] loop = [(241, 202), (218, 173), (218, 127), (242, 272)];
        var jumping = LabyrinthTrials.LinkedPath(loop, [(0, 1, false), (1, 2, false), (2, 3, true), (3, 0, false)], 0);
        Assert.True(jumping.Ring);
        Assert.Equal([((218, 127), (242, 272))], jumping.Jumps);
    }
}
