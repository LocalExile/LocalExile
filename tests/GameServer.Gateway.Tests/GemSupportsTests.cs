using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class GemSupportsTests
{
    static string Data => FindData();
    static string FindData()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/GrantedEffects.json"))) return Path.Combine(dir.FullName, "data");
        throw new InvalidOperationException("Extract game tables before running gem support tests");
    }

    [Theory]
    [InlineData("Cyclone", "SupportIncreasedAreaOfEffect", true)]
    public void Compatibility_uses_native_skill_types(string active, string support, bool expected)
    {
        var skills = new SkillCatalog(Data);
        var catalog = new GemSupports(Data);
        var result = catalog.Resolve(skills.EffectRow(active), [new(skills.EffectRow(support), 20, 0, 1)], skills);
        Assert.Equal(expected, result.Count == 1);
    }

    [Fact]
    public void Physical_more_follows_gain_history_and_conditional_more_is_not_applied_to_every_hit()
    {
        var hit = DamagePacket.Empty.Add(DamageType.Physical, new(100, 100));
        (string, int)[] mods = [("support_melee_physical_damage_+%_final", 49),
            ("support_ruthless_big_hit_damage_+%_final", 100), ("support_burning_damage_+%_final", 100)];
        var conversion = new DamageConversion(); conversion.AddGain(DamageType.Physical, DamageType.Fire, 39);
        hit = PlayerHitDamage.Resolve(hit, _ => 0, conversion, spell: false, melee: true, supportModifiers: mods);
        Assert.Equal(149, hit[DamageType.Physical].Min);
        Assert.Equal(149 * .39, hit[DamageType.Fire].Min, 8);
    }

}
