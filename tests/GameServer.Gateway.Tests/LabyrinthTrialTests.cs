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
