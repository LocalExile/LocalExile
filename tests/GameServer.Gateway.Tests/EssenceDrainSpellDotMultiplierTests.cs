namespace GameServer.Gateway.Tests;

public sealed class EssenceDrainSpellDotMultiplierTests
{
    [Fact]
    public void Spell_damage_over_time_multiplier_scales_essence_drain_alongside_damage_increase()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var extra = new Dictionary<uint, int>();
        using var stats = GameEvents.PlayerStatSources.Add("Game", (p, values) =>
        {
            if (ReferenceEquals(p, player))
                foreach (var (k, v) in extra) values[k] = values.GetValueOrDefault(k) + v;
            return values;
        });
        ushort skill = Assert.Single(f.Equip(("Essence Drain", 1)));
        int effect = Game.SupportedSkill(player, skill).Effect;
        double Dps() => Assert.IsType<MonsterEssenceDrain.Cast>(Game.EssenceDrainCast(player, skill, effect)).DamagePerSecond;
        extra[Stat.DamageOverTimePlusPct] = 12;
        double increased = Dps();
        extra[Stat.DotMultiplierPlus] = 4;
        double expected = Dps();
        Assert.True(expected > increased);

        extra.Remove(Stat.DotMultiplierPlus);
        extra[Stat.DamageOverTimeMultiplierPlusWithSpells] = 4;
        Assert.Equal(expected, Dps(), 6);
    }
}
