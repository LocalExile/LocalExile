using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ArmourAttackDamageTests
{
    const string PerArmour = "attack_damage_+%_per_450_physical_damage_reduction_rating";
    static readonly StatCatalog Catalog = new();
    static readonly SkillCatalog.Unarmed Weapon = new(1000, 100, 100, 20);
    static uint Key(string name) => Catalog.Key(name)!.Value;
    static int Value(IReadOnlyDictionary<uint, int> stats, string name) => stats.GetValueOrDefault(Key(name));

    sealed class Character : IDisposable
    {
        public ClientState State { get; }
        public Character(int armour)
        {
            TestSaves.Ensure();
            State = new(new(0, "ArmourAttack", 128, 128, 32, 32, null, []), 0, 1)
            { Character = "ArmourAttack" + Guid.NewGuid().ToString("N"), ClassRow = 1,
                Pools = new(100, 50, 1000, 0, 0, Armour: armour,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000) };
            State.Passives.Add(2103);
        }
        public Dictionary<uint, int> Stats() => Program.CharacterPlayerStats(State);
        public void Dispose() => PlayerInventories.Forget(State.Character!);
    }

    [Fact]
    public void The_real_Steel_Willed_node_supplies_two_percent_per_complete_450_final_armour()
    {
        var node = new PassiveTree().Find(2103);
        Assert.NotNull(node);
        Assert.Equal("Steel Willed", node.Name);
        Assert.True(node.IsAscendancy);
        Assert.Equal(13019u, Key(PerArmour));
        Assert.Contains((Key(PerArmour), 2), node.Stats);
        using var character = new Character(900);
        Assert.Equal(4, Value(character.Stats(), "attack_damage_+%"));
    }

}
