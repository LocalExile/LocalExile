namespace GameServer.Gateway.Tests;

public sealed class DelveGenerationTests
{
    [Fact]
    public void Mine_monkey_mortar_writes_empty_state_machine_before_tasks()
    {
        var blob = new GameServer.Core.Protocol.Instance.ProjectileObject("Metadata/Projectiles/DelveMonkeyMortar", 172, 1460, Height: 8)
            { StateMachine = new([], 0) }.Blob();
        Assert.Equal(30, blob.Length);
        Assert.Equal(new byte[] { 0, 0, 0 }, blob[^3..]);
    }

    [Fact]
    public void Cells_without_exits_use_native_empty_graph_even_with_a_feature()
    {
        // A nonzero feature does not make an isolated cell an encounter room in the client.
        byte[] terrain = new byte[25];
        terrain[0] = 0x20; terrain[8] = 0x13;
        terrain[15] = 13;
        terrain[20] = 7; terrain[21] = 0x10;
        var pass = new GameServer.Data.Terrain.Generation.DelveGeneration(new(_ => null), terrain);
        var graph = new GameServer.Data.Terrain.Generation.TileGraph();
        typeof(GameServer.Data.Terrain.Generation.DelveGeneration)
            .GetMethod("Prepare", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(pass, [graph, GameServer.Data.Terrain.Generation.DelveGeneration.Main, 0,
                new GameServer.Data.Terrain.Generation.LevelRandom(1)]);
        Assert.Equal(new[] {
            GameServer.Data.Terrain.Generation.DelveGeneration.EmptyCellGraph,
            GameServer.Data.Terrain.Generation.DelveGeneration.EmptyCellGraph,
            GameServer.Data.Terrain.Generation.DelveGeneration.CellGraph,
        }, graph.Nodes.Select(n => n.Descriptions[0]));
    }

}
