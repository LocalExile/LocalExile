using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class EssenceDrainSpellDotIncreaseTests
{
    [Fact]
    public void Spell_damage_over_time_increase_scales_the_debuff_and_leaves_the_initial_hit_alone()
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
        double initialHit = Game.SupportedHitFor(player, skill, suppress: false)[DamageType.Chaos].Max;
        double baseline = Dps();
        extra[Stat.DamageOverTimePlusPct] = 12;
        double expected = Dps();
        Assert.True(expected > baseline);

        extra.Remove(Stat.DamageOverTimePlusPct);
        extra[Stat.DamageOverTimePlusPctWithSpells] = 12;
        Assert.Equal(expected, Dps(), 6);
        Assert.Equal(initialHit, Game.SupportedHitFor(player, skill, suppress: false)[DamageType.Chaos].Max, 6);
    }
}
