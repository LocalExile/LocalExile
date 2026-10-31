using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class LeechSourcesTests
{
    [Theory]
    [InlineData("life")]
    [InlineData("mana")]
    [InlineData("energy_shield")]
    public void Raw_scopes_add_once_per_final_damage_type(string pool)
    {
        Dictionary<string, int> raw = new()
        {
            [$"{pool}_leech_from_any_damage_permyriad"] = 100,
            [$"base_{pool}_leech_from_fire_damage_permyriad"] = 200,
            [$"base_{pool}_leech_from_elemental_damage_permyriad"] = 300,
            [$"base_{pool}_leech_from_spell_damage_permyriad"] = 400,
            [$"base_{pool}_leech_from_attack_damage_permyriad"] = 500,
            [$"{pool}_leech_from_physical_attack_damage_permyriad"] = 600,
            ["life_and_mana_leech_from_physical_damage_permyriad"] = 700,
            ["attack_skill_mana_leech_from_any_damage_permyriad"] = 800,
            // Derived aliases deliberately contain enormous values: adding them would double-count the sources.
            [$"{pool}_leech_from_fire_damage_permyriad"] = 999999,
            [$"virtual_{pool}_leech_from_attack_damage_permyriad"] = 999999,
        };
        DamagePacket Coefficients(bool attack)
        {
            var sources = LeechSources.From(n => raw.GetValueOrDefault(n), attack, _ => 0);
            return pool == "life" ? sources.Life : pool == "mana" ? sources.Mana : sources.EnergyShield;
        }
        var spell = Coefficients(false);
        Assert.Equal(1000, spell[DamageType.Fire].Min);
        Assert.Equal(800, spell[DamageType.Cold].Min);
        Assert.Equal(500, spell[DamageType.Physical].Min);
        var attack = Coefficients(true);
        int mana = pool == "mana" ? 800 : 0;
        Assert.Equal(1100 + mana, attack[DamageType.Fire].Min);
        Assert.Equal(1200 + mana + (pool == "energy_shield" ? 0 : 700), attack[DamageType.Physical].Min);
    }

    [Fact]
    public void Fractional_raw_sources_and_qualified_local_sources_survive_without_spell_leakage()
    {
        int Get(string n) => n == "horrible_ugly_life_leech_from_fire_damage_perhundredthousand" ? 6 : 0;
        int Local(string n) => n == "local_life_leech_from_any_damage_permyriad" ? 10 : 0;
        Assert.Equal(10.6, LeechSources.From(Get, true, Local).Life[DamageType.Fire].Min, 8);
        Assert.Equal(.6, LeechSources.From(Get, false, Local).Life[DamageType.Fire].Min, 8);
    }

    [Fact]
    public void Typed_post_mitigation_weights_follow_actual_direct_total_including_critical_rounding()
    {
        var source = LeechSources.From(n => n == "base_life_leech_from_fire_damage_permyriad" ? 10000 : 0, false);
        var taken = DamagePacket.Of(DamageType.Fire, 25, 25).Add(DamagePacket.Of(DamageType.Physical, 75, 75));
        Assert.Equal((37.5, 0d, 0d), source.Amounts(taken, 150));
        Assert.Equal(default, source.Amounts(taken, 0));
        Assert.Equal(default, source.Amounts(DamagePacket.Empty, 150));
    }

    [Fact]
    public void Bow_direct_callback_excludes_reflected_impales()
    {
        var shot = new OrdinaryRangedShot(DamagePacket.Of(DamageType.Physical, 100, 100), 100, 0, 150, 0);
        var impale = new ImpaleState(); impale.Apply(500);
        int direct = 0;
        var hit = shot.Roll(impale, new Random(1), directHit: amount => direct = amount);
        Assert.Equal(100, direct);
        Assert.Equal(150, hit.Damage);
    }
}
