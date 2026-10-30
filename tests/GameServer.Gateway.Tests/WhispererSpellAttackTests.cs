using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class WhispererSpellAttackTests
{
    const string Flag = "additive_spell_damage_modifiers_apply_to_attack_damage_at_150%_value";
    static readonly StatCatalog Catalog = new();
    static readonly SkillCatalog.Unarmed Weapon = new(1000, 100, 100, 20);

    static Dictionary<uint, int> Stats(params (string Name, int Value)[] values) =>
        values.ToDictionary(x => Catalog.Key(x.Name)!.Value, x => x.Value);

    static Func<string, int> Reader(IReadOnlyDictionary<uint, int> stats) =>
        name => Catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    [Fact]
    public void The_real_Whisperer_node_enables_the_fixed_transfer_stat()
    {
        var node = new PassiveTree().Find(50932);
        Assert.NotNull(node);
        Assert.Equal("your mind is destroyed", node.Name);
        Assert.True(node.IsAscendancy);
        Assert.Equal(11811u,
            Catalog.Key(Flag));
        Assert.Equal(26u, Catalog.Key("spell_damage_+%"));
        Assert.Contains((Catalog.Key(Flag)!.Value, 1), node.Stats);
        var sources = new PassiveTree().Stats([50932]);
        sources[Catalog.Key("spell_damage_+%")!.Value] = 20;
        Assert.Equal(130, Weapon.HitDamage(sources)[DamageType.Physical].Min);
    }

}
