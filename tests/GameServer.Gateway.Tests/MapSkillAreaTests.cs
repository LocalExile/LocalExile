namespace GameServer.Gateway.Tests;

public sealed class MapSkillAreaTests
{
    [Fact]
    public void Map_less_area_multiplies_existing_increased_and_more_area_before_radius_rounding()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        ushort curse = Assert.Single(fixture.Equip(("Flammability", 1)));
        conn.PlayerStats[Program.Stats.Key("skill_area_of_effect_+%")!.Value] = 50;
        conn.PlayerStats[Program.Stats.Key("skill_area_of_effect_+%_final")!.Value] = 20;
        int Radius() => Game.CurseRadius(conn, curse, aura: false);
        Assert.Equal(29, Radius());
        var area = new MapDevice.MapArea(conn.Layout.Hash, "MapWorldsStrand", "Test map", 17);
        var mods = new MapMods(new Dictionary<string, int> { [SkillArea.MapAreaFinal] = -30 });
        conn.Instance!.OpenedWith(new MapDevice.Run(conn.Character!, area, mods));
        Assert.Equal(24, Radius());
        conn.Instance = new();
        Assert.Equal(29, Radius());
    }
}
