using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class EssenceDrainSpellDotIncreaseTests
{
    [Fact]
    public void Spell_damage_over_time_increase_scales_the_debuff_and_leaves_the_initial_hit_alone()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        ushort skill = Assert.Single(f.Equip(("Essence Drain", 1)));
        int effect = Game.SupportedSkill(conn, skill).Effect;
        double Dps() => Assert.IsType<MonsterEssenceDrain.Cast>(Game.EssenceDrainCast(conn, skill, effect)).DamagePerSecond;
        double initialHit = Game.SupportedHitFor(conn, skill, suppress: false)[DamageType.Chaos].Max;
        double baseline = Dps();
        uint generic = Program.Stats.Key("damage_over_time_+%")!.Value;
        conn.PlayerStats[generic] = 12;
        double expected = Dps();
        Assert.True(expected > baseline);

        conn.PlayerStats.Remove(generic);
        uint spellDot = Program.Stats.Key("damage_over_time_+%_with_spells")!.Value;
        conn.PlayerStats[spellDot] = 12;
        Assert.Equal(expected, Dps(), 6);
        Assert.Equal(initialHit, Game.SupportedHitFor(conn, skill, suppress: false)[DamageType.Chaos].Max, 6);
    }
}
