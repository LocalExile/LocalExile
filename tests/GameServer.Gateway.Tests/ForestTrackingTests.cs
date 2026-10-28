using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ForestTrackingTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    static int Value(IReadOnlyDictionary<uint, int> stats, string name) => stats.GetValueOrDefault(Key(name));
    static ClientState Character(int evasion)
    {
        TestSaves.Ensure();
        var state = new ClientState(new(0, "Forest", 128, 128, 32, 32, null, []), 0, 1)
        { ClassRow = 3, Pools = new(100, 50, 1, 0, 0, Evasion: evasion) };
        state.Passives.Add(6281);
        return state;
    }

    [Theory]
    [InlineData(-450, 0)]
    [InlineData(0, 0)]
    [InlineData(449, 0)]
    [InlineData(450, 3)]
    [InlineData(899, 3)]
    [InlineData(900, 6)]
    [InlineData(45000, 300)]
    public void Real_node_counts_complete_groups_for_weapon_added_and_supported_attacks(int evasion, int expected)
    {
        var node = Program.Passives.Find(6281)!;
        Assert.Equal("Forest Tracking", node.Name);
        Assert.True(node.IsAscendancy);
        Assert.Equal(2434u, Key(ArmourAttackDamage.Per450Evasion));
        Assert.Contains((2434u, 3), node.Stats);
        var state = Character(evasion);
        state.PlayerStats[Key("attack_minimum_added_chaos_damage")] = 100;
        state.PlayerStats[Key("attack_maximum_added_chaos_damage")] = 100;
        var stats = Program.CharacterPlayerStats(state);
        Assert.Equal(expected, Value(stats, "attack_damage_+%"));
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 20);
        var hit = Program.AttackHit(weapon, stats, melee: false);
        Assert.Equal(100 + expected, hit[DamageType.Physical].Min, 6);
        Assert.Equal(100 + expected, hit[DamageType.Chaos].Min, 6);
        var supported = SupportedAttackDamage.Hit(weapon, name => Value(stats, name), new HashSet<int>());
        Assert.Equal(hit.MinTotal, supported.MinTotal, 6);
        var gem = AttackSkillHit.Resolve(weapon, stats, [], Program.Stats, 15000);
        Assert.Equal((100 + expected) * 1.5, gem[DamageType.Physical].Min, 6);
        var spell = SpellDamage.Packet(DamagePacket.Of(DamageType.Fire, 100, 100), name => Value(stats, name));
        Assert.Equal(100, spell.MinTotal);
    }

    [Fact]
    public void Rebuild_removal_overrides_and_Armour_contributions_share_one_raw_attack_bucket()
    {
        var state = Character(900);
        state.Passives.Add(2103);
        state.Pools = state.Pools with { Armour = 900 };
        state.PlayerStats[Key("attack_damage_+%")] = 10;
        int Attack() => Value(Program.CharacterPlayerStats(state), "attack_damage_+%");
        Assert.Equal(20, Attack()); // Raw 10 + Steel Willed 4 + Forest Tracking 6.
        Assert.Equal(20, Attack());
        Assert.Equal(10, state.PlayerStats[Key("attack_damage_+%")]);
        state.PlayerStats[Key("evasion_rating")] = 449;
        Assert.Equal(14, Attack());
        state.PlayerStats.Remove(Key("evasion_rating"));
        state.PlayerStats[Key(ArmourAttackDamage.Per450Evasion)] = 0;
        Assert.Equal(14, Attack());
        state.PlayerStats.Remove(Key(ArmourAttackDamage.Per450Evasion));
        state.Passives.Remove(6281);
        Assert.Equal(14, Attack());
        state.Passives.Clear();
        Assert.Equal(10, Attack());
    }

    [Fact]
    public void Forest_damage_and_Whaletooth_duration_are_derived_once_in_the_same_snapshot()
    {
        var state = Character(900);
        state.PlayerStats[Key("strength")] = 455;
        state.PlayerStats[Key(SkillDurationModifiers.PerTenStrength)] = 1;
        for (int i = 0; i < 2; i++)
        {
            var stats = Program.CharacterPlayerStats(state);
            Assert.Equal(6, Value(stats, "attack_damage_+%"));
            Assert.Equal(45, Value(stats, SkillDurationModifiers.IncreasedDuration));
        }
        state.PlayerStats[Key(SkillDurationModifiers.IncreasedDuration)] = 0;
        Assert.Equal(0, Value(Program.CharacterPlayerStats(state), SkillDurationModifiers.IncreasedDuration));
        Assert.Equal(6, Value(Program.CharacterPlayerStats(state), "attack_damage_+%"));
    }

    [Theory]
    [InlineData(0, 450, int.MaxValue, 450, int.MinValue, -1)]
    [InlineData(10, 900, int.MaxValue, 900, int.MinValue, 8)]
    [InlineData(int.MaxValue, 0, 0, 450, int.MinValue, -1)]
    [InlineData(0, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(0, int.MaxValue, int.MinValue, int.MaxValue, int.MinValue, int.MinValue)]
    public void Both_defence_bonuses_cancel_before_the_single_final_clamp(
        int attack, int armour, int perArmour, int evasion, int perEvasion, int expected) =>
        Assert.Equal(expected, ArmourAttackDamage.Total(attack, armour, perArmour, evasion, perEvasion));

    [Fact]
    public void Final_evasion_includes_existing_increases_and_becomes_zero_after_Iron_Reflexes()
    {
        var state = Character(0);
        state.Passives.Add(2103);
        var bonus = new CharacterStats.Bonus(Armour: 225, ArmourPct: 100, Evasion: 285, EvasionPct: 198);
        state.Pools = CharacterStats.For(Program.Skills.ClassFor(1), 1, bonus);
        var before = Program.CharacterPlayerStats(state);
        Assert.Equal(450, Value(before, "physical_damage_reduction_rating"));
        Assert.Equal(900, Value(before, "evasion_rating"));
        Assert.Equal(8, Value(before, "attack_damage_+%"));
        state.Pools = CharacterStats.For(Program.Skills.ClassFor(1), 1, bonus with { IronReflexes = true });
        var after = Program.CharacterPlayerStats(state);
        Assert.Equal(0, Value(after, "evasion_rating"));
        Assert.Equal(state.Pools.Armour / 450 * 2, Value(after, "attack_damage_+%"));
    }
}
