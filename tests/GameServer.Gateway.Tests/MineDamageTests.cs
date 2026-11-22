using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MineDamageTests
{
    [Fact]
    public void Mine_damage_scales_fireball_projectiles_only_with_blastchain_mine_support()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        ushort mine = Assert.Single(f.Equip(("Fireball", 1), ("Blastchain Mine Support", 1)));
        uint generic = Program.Stats.Key("damage_+%")!.Value;
        uint mineStat = Program.Stats.Key("mine_damage_+%")!.Value;
        double FireDamage(ushort skill) =>
            Game.SupportedHitFor(conn, skill, suppress: false, projectile: true)[DamageType.Fire].Max;

        Assert.True(Game.PlacedKind(Game.SupportedSkill(conn, mine)).Mine);
        double baseline = FireDamage(mine);
        conn.PlayerStats[generic] = 14;
        double control = FireDamage(mine);
        Assert.True(control > baseline);

        conn.PlayerStats.Remove(generic);
        conn.PlayerStats[mineStat] = 14;
        Assert.Equal(control, FireDamage(mine), 6);
        ushort selfCast = Assert.Single(f.Equip(("Fireball", 1)));
        Assert.False(Game.PlacedKind(Game.SupportedSkill(conn, selfCast)).Mine);
        double withMineStat = FireDamage(selfCast);
        conn.PlayerStats.Remove(mineStat);
        Assert.Equal(FireDamage(selfCast), withMineStat, 6);
    }
}
