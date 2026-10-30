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

}
