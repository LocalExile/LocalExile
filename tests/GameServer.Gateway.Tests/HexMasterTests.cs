namespace GameServer.Gateway.Tests;

public sealed class HexMasterTests
{
    [Fact]
    public void Hex_master_applies_its_effect_and_keeps_hex_replacement_and_new_casts_working()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        fixture.Equip(("Flammability", 1), ("Frostbite", 1));
        ushort flammability = Game.GemSkillEffects(conn).Single(skill => skill.Name == "Flammability").Id;
        ushort frostbite = Game.GemSkillEffects(conn).Single(skill => skill.Name == "Frostbite").Id;
        var target = new SpawnedMonster("hex target", new(100, 100, 0), 0, 0, 1000);
        uint keystone = Program.Stats.Key("keystone_hex_master")!.Value;
        Assert.True(Game.ApplyCurseLocked(conn, target, flammability, 1, aura: false));
        var normal = Assert.Single(target.Curses.All.Values);
        Assert.NotEmpty(normal.Stats);

        conn.PlayerStats[keystone] = 1;
        Assert.True(Game.ApplyCurseLocked(conn, target, flammability, 2, aura: false));
        target.Curses.Expire(30);
        var permanent = Assert.Single(target.Curses.All.Values);
        Assert.Equal(double.PositiveInfinity, permanent.Until);
        foreach (var (stat, value) in normal.Stats)
            Assert.Equal((int)Math.Truncate(value * .8), permanent.Stats[stat]);
        Assert.True(Game.ApplyCurseLocked(conn, target, frostbite, 31, aura: false));
        Assert.Equal("Frostbite", Assert.Single(target.Curses.All.Values).Effect);
        conn.PlayerStats.Remove(keystone);
        Assert.True(Game.ApplyCurseLocked(conn, target, flammability, 32, aura: false));
        target.Curses.Expire(60);
        Assert.Empty(target.Curses.All);
    }
}
