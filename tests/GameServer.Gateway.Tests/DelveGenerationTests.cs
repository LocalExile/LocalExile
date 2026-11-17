namespace GameServer.Gateway.Tests;

public sealed class DelveGenerationTests
{
    [Theory]
    [InlineData("HeistThugTorchBombMortar", true)]
    [InlineData("DelveMonkeyMortar", true)]
    [InlineData("AbyssBoneHuskMortar", false)]
    public void Mortars_write_only_the_components_the_client_reads(string name, bool hasStateMachine)
    {
        const uint id = 123;
        var type = "Metadata/Projectiles/" + name;
        var mortar = new GameServer.Core.Protocol.Instance.ProjectileObject(type, 172, 1460, Height: 8);
        var expected = hasStateMachine ? mortar with { StateMachine = new([], 0) } : mortar;
        var blob = expected.Blob();
        Assert.Equal(hasStateMachine ? 30 : 28, blob.Length);
        Assert.Equal(GameServer.Core.Protocol.Instance.InstanceEntities.CreateObject(id,
            GameServer.Core.Protocol.Instance.InstanceEntities.ObjectTypeHash(type), blob, headerU32: uint.MaxValue),
            Game.CreateProjectileObject(id, mortar));
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
