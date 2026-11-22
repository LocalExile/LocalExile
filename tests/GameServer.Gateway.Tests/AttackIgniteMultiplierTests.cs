using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class AttackIgniteMultiplierTests
{
    [Fact]
    public void Burning_brutality_scales_attack_ignite()
    {
        var raw = DamagePacket.Of(DamageType.Fire, 100, 100);
        static int Get(string name) => name == "fire_damage_over_time_multiplier_+_with_attacks" ? 12 : 0;
        int Ignite(string hand, bool spell) => AilmentPanelStats.Compute(new(hand, spell), raw, new(), Get)
            .Single(row => row.Stat == $"maximum_ignite_damage_to_deal_per_minute_from_non_critical_{hand}_damage").Value;

        Assert.Equal(6048, Ignite("main_hand", spell: false));
        Assert.Equal(5400, Ignite("spell", spell: true));
        Assert.Equal(100, PlayerHitDamage.Resolve(raw, Get, new(), spell: false)[DamageType.Fire].Max);
    }
}
