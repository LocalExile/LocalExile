using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SpellPoisonMultiplierTests
{
    [Fact]
    public void Serpentine_spellslinger_scales_poison_inflicted_by_spells()
    {
        var raw = DamagePacket.Of(DamageType.Physical, 100, 100);
        static int Get(string name) => name == "poison_dot_multiplier_+_with_spells" ? 12 : 0;
        int Poison(string hand, bool spell) => AilmentPanelStats.Compute(new(hand, spell), raw, new(), Get)
            .Single(row => row.Stat == $"maximum_poison_damage_to_deal_per_minute_from_non_critical_{hand}_damage").Value;

        Assert.Equal(2016, Poison("spell", spell: true));
        Assert.Equal(1800, Poison("main_hand", spell: false));
        Assert.Equal(100, PlayerHitDamage.Resolve(raw, Get, new(), spell: true)[DamageType.Physical].Max);
    }
}
