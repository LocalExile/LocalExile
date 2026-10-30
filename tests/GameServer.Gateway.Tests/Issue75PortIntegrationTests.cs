using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData(50, 1350)]
    [InlineData(60, 1500)]
    [InlineData(100, 2100)]
    public void Issue75_Mageblood_uses_the_rebuilt_level_and_raw_flask_effect_sources(int level, int armour)
    {
        using var game = LevelPanelCharacter(50); // Qualifies for Mageblood's native level requirement.
        var items = PlayerInventories.For(game.Character, Program.Items);
        items.Add(PlayerInventories.Belt, 0, 0, Program.Uniques.Create("Mageblood", new Random(1))!);
        var flask = ItemCatalog.Create(Program.Items.Find("Granite Flask")!) with { Rarity = ItemRarity.Magic };
        items.Add(PlayerInventories.Flask, 0, 0, flask);
        var raw = game.State.PlayerStats;
        raw[Program.Stats.Key("flask_effect_+%")!.Value] = -60;
        raw[Program.Stats.Key("flask_effect_+%_per_level")!.Value] = 1;

        // The requested level must win while conn.Pools still holds the previous level.
        var rebuilt = Program.PoolsFor(game.State, level);
        Assert.Equal(armour, rebuilt.Armour);
        game.State.Pools = rebuilt;
        Assert.Equal(armour, Program.CharacterPlayerStats(game.State)[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);

        raw[Program.Stats.Key("flask_effect_+%_per_level")!.Value] = 0;
        game.State.Pools = Program.PoolsFor(game.State, level);
        Assert.Equal(600, game.State.Pools.Armour);
        Assert.Equal(600, Program.CharacterPlayerStats(game.State)[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
    }

    [Fact]
    public async Task Issue75_raw_attribute_off_and_inherent_prohibition_preserve_CI_and_chaos_armour()
    {
        using var game = LevelPanelCharacter(100);
        // A real passive source for this composition fixture; allocation/path legality is tested separately.
        const ushort chaosInoculation = 11455;
        Assert.Contains(Program.Passives.Find(chaosInoculation)!.Stats,
            stat => stat.Key == Program.Stats.Key("keystone_chaos_inoculation") && stat.Value == 1);
        game.State.Passives.Add(chaosInoculation);
        game.State.Pools = Program.PoolsFor(game.State, 100);
        Assert.Null((await game.Command("/set_stat all_attributes_+% 100")).Error);
        Assert.Null((await game.Command($"/set_stat {InherentAttributeBonuses.NoStrengthBonuses} 1")).Error);
        Assert.Null((await game.Command($"/set_stat {PlayerHitMitigation.ArmourAppliesToChaos} 1")).Error);
        Assert.Equal(28, game.State.Pools.Strength);
        Assert.Equal(0, game.State.Pools.MeleePhysicalDamagePct);
        Assert.Equal(1u, game.State.Pools.Life);
        Assert.True(game.State.Pools.ArmourAppliesToChaosDamage);
        Assert.Null((await game.Command("/set_stat all_attributes_+% off")).Error);
        Assert.Equal(14, game.State.Pools.Strength);
        Assert.Equal(0, game.State.Pools.MeleePhysicalDamagePct);
        Assert.Equal(1u, game.State.Pools.Life);
        Assert.True(game.State.Pools.ArmourAppliesToChaosDamage);
        Assert.Null((await game.Command($"/set_stat {InherentAttributeBonuses.NoStrengthBonuses} off")).Error);
        Assert.Equal(2, game.State.Pools.MeleePhysicalDamagePct);
    }
}

public sealed class Issue75WeaponPortTests
{
    [Fact]
    public void Both_local_elemental_buckets_survive_the_shared_weapon_calculator()
    {
        var weapon = ItemWeapons.Of(1000, 9, 9, 8, new Dictionary<string, int>
        {
            ["local_minimum_added_fire_damage"] = 4, ["local_maximum_added_fire_damage"] = 8,
            ["local_minimum_added_chaos_damage"] = 2, ["local_maximum_added_chaos_damage"] = 4,
            ["local_elemental_damage_+%"] = 50,
            ["local_attacks_with_this_weapon_elemental_damage_+%"] = 25,
            ["local_physical_damage_+%"] = 100,
        }, 20);
        Assert.Equal(new DamageRange(22, 22), weapon.LocalDamage![DamageType.Physical]);
        Assert.Equal(new DamageRange(7, 14), weapon.LocalDamage[DamageType.Fire]);
        Assert.Equal(new DamageRange(2, 4), weapon.LocalDamage[DamageType.Chaos]);
    }
}
