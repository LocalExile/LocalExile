namespace GameServer.Gateway.Tests;

public sealed class MapPlayerDefencesTests
{
    [Fact]
    public void Map_defences_apply_a_final_multiplier_and_restore_on_leaving()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        var vest = ItemCatalog.Create(Program.Items.Find("Plate Vest")!, random: new Random(11));
        Game.ItemsOf(conn).Put(PlayerInventories.BodyArmour, new(100, 0, 0, vest));
        conn.PlayerStats[Program.Stats.Key("base_physical_damage_reduction_rating")!.Value] = 200;
        conn.PlayerStats[Program.Stats.Key("global_defences_+%")!.Value] = 100;
        var outside = Game.BuildPoolsFor(conn, 90);
        Assert.True(outside.Armour > 200 && outside.Evasion > 0);
        var area = new MapDevice.MapArea(conn.Layout.Hash, "MapWorldsStrand", "Test map", 17);
        var mods = new MapMods(new Dictionary<string, int> { ["map_player_global_defences_+%"] = -30 });
        conn.Instance!.OpenedWith(new MapDevice.Run(conn.Character!, area, mods));
        var inside = Game.BuildPoolsFor(conn, 90);
        static long Scale(long value) => (70 * value + 50) / 100;
        Assert.Equal(Scale(outside.Armour), inside.Armour);
        Assert.Equal(Scale(outside.Evasion), inside.Evasion);
        Assert.Equal(Scale(outside.EnergyShield), inside.EnergyShield);
        Assert.Equal(Scale(outside.Ward), inside.Ward);
        Assert.Equal((outside.Life, outside.Mana), (inside.Life, inside.Mana));
        var returned = new ClientState(conn.Layout, 2, 1)
            { Instance = new(), Character = conn.Character, Pools = conn.Pools, LeagueModule = conn.LeagueModule };
        foreach (var row in conn.PlayerStats) returned.PlayerStats[row.Key] = row.Value;
        Assert.Equal(outside.Armour, Game.BuildPoolsFor(returned, 90).Armour);
    }
}
