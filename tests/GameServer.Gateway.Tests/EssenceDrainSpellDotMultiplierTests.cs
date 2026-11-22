namespace GameServer.Gateway.Tests;

public sealed class EssenceDrainSpellDotMultiplierTests
{
    [Fact]
    public void Spell_damage_over_time_multiplier_scales_essence_drain_alongside_damage_increase()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        ushort skill = Assert.Single(f.Equip(("Essence Drain", 1)));
        int effect = Game.SupportedSkill(conn, skill).Effect;
        double Dps() => Assert.IsType<MonsterEssenceDrain.Cast>(Game.EssenceDrainCast(conn, skill, effect)).DamagePerSecond;
        conn.PlayerStats[Program.Stats.Key("damage_over_time_+%")!.Value] = 12;
        double increased = Dps();
        uint generic = Program.Stats.Key("dot_multiplier_+")!.Value;
        conn.PlayerStats[generic] = 4;
        double expected = Dps();
        Assert.True(expected > increased);

        conn.PlayerStats.Remove(generic);
        uint spellMultiplier = Program.Stats.Key("damage_over_time_multiplier_+_with_spells")!.Value;
        conn.PlayerStats[spellMultiplier] = 4;
        Assert.Equal(expected, Dps(), 6);
    }
}
