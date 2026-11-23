using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class WorldAreaTownsTests
{
    // HASH16 values of the 3.29.3b WorldAreas rows.
    const ushort LioneyesWatch = 0x235c, ForestEncampment = 0x2233, TwilightStrand = 0xf00a;

    static ClientTable WriteTable(string json) => TestTables.Rows("WorldAreas", json);

    [Fact]
    public void Loads_the_town_rows_and_skips_the_rest()
    {
        var areas = WriteTable($$"""
            [{"Id":"1_1_town","IsTown":true,"HASH16":{{LioneyesWatch}}},
             {"Id":"1_1_2","IsTown":false,"HASH16":{{0x7ad2}}},
             {"Id":"1_2_town","IsTown":true,"HASH16":{{ForestEncampment}}},
             {"Id":"pvpTown","IsTown":true,"HASH16":{{0xee9}}}]
            """);
        Assert.Equal([("1_1_town", LioneyesWatch), ("1_2_town", ForestEncampment)], WorldAreaTowns.Load(areas));
    }

    [Fact]
    public void A_loaded_town_has_one_shared_instance_where_the_default_registry_makes_two()
    {
        var areas = WriteTable($$"""
            [{"Id":"1_1_town","IsTown":true,"HASH16":{{LioneyesWatch}}},
             {"Id":"1_2_town","IsTown":true,"HASH16":{{ForestEncampment}}}]
            """);
        var before = new InstanceRegistry { SeedFor = _ => 7 };
        Assert.NotSame(before.Enter(ForestEncampment, "A"), before.EnterNew(ForestEncampment, "B"));

        var after = new InstanceRegistry { SeedFor = _ => 7, Towns = WorldAreaTowns.Load(areas).Select(t => t.Hash16).ToHashSet() };
        var a = after.Enter(ForestEncampment, "A");
        Assert.Same(a, after.EnterNew(ForestEncampment, "B"));
        Assert.True(a.IsTown);
        Assert.Equal(1u, a.Seed);
    }

    [Theory]
    [InlineData("1_2_town", ForestEncampment)]   // the last town entered
    public void Login_area_is_the_last_town_else_the_starting_area(string? lastTown, ushort expected)
    {
        IReadOnlyList<(string, ushort)> towns = [("1_1_town", LioneyesWatch), ("1_2_town", ForestEncampment)];
        Assert.Equal(expected, WorldAreaTowns.LoginArea(lastTown, towns));
    }

    [Fact]
    public void Without_the_table_only_Lioneyes_Watch_is_a_town()
    {
        Assert.Equal([("1_1_town", LioneyesWatch)], WorldAreaTowns.Load(null));
    }
}
