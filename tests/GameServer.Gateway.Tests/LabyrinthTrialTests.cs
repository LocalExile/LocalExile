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

        // Positioned (18), Animated (1 + u32 count + UTF-16), InteractionAction, Transitionable, TriggerableBlockage, tasks.
        int animated = 2 + 18, after = animated + 1 + 4 + model.Length * 2;
        Assert.Equal(160, BitConverter.ToInt32(closed, 2));
        Assert.Equal(1, closed[animated]);
        Assert.Equal(model.Length, BitConverter.ToInt32(closed, animated + 1));
        Assert.Equal(new byte[] { 0, LabyrinthTrials.Closed, 1, 0, 0 }, closed[after..]);
        Assert.Equal(new byte[] { 0, LabyrinthTrials.Open, 0, 0 }, open[after..]);
        Assert.Equal(4, LabyrinthTrials.TransitionableIndex(LabyrinthTrials.IntroDoor));
        Assert.Equal(1, LabyrinthTrials.TransitionableIndex(LabyrinthTrials.Lever));

        // 0x1c6: id, world header, u8 0 (unblocked: no sections follow).
        Assert.Equal(new byte[] { 0x01, 0xc6, 0, 0, 0x30, 0x05, 0, 0, 0, 0, 0, 0, 0 }, InstanceEntities.BlockageUpdate(0x3005, false));
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
}
