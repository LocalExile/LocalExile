using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using DamageType = GameServer.Core.World.DamageType;

namespace GameServer.Gateway.Tests;

// Task #222 followup: the shared player resolver applies attack_damage_+% (every attack) and uses
// correct melee context: melee_damage_+% applies to actual melee hits only — an attack gem by its own
// ActiveSkillType Melee classification (SkillCatalog.IsMelee), the default attack and the panel's main-hand rows
// by the active weapon set's weapon profile, where an inactive weapon is not there at all and unarmed swings are
// melee. No inference from the weapon's range.
public class MeleeContextTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static Dictionary<uint, int> ManaStats(StatCatalog catalog, int maxMana) =>
        new() { [catalog.Key("maximum_mana")!.Value] = maxMana, [catalog.Key(DamageStats.MaxManaAddedLightning)!.Value] = 6 };

    static Func<string, int> Reader(StatCatalog catalog, IReadOnlyDictionary<uint, int> stats) =>
        name => catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    static DamagePacket Hit(DamagePacket raw, Func<string, int> get, bool spell, bool melee) =>
        PlayerHitDamage.Resolve(DamageStats.RawAdded(raw, get, spell), get, new(), spell, melee);

    [Fact]
    public void Generic_attack_damage_scales_the_added_pool_bonus_like_the_type_stat()
    {
        var catalog = new StatCatalog(Data());
        var stats = ManaStats(catalog, 1000);
        stats[catalog.Key("attack_damage_+%")!.Value] = 50;

        var packet = Hit(DamagePacket.Empty, Reader(catalog, stats), spell: false, melee: false);

        Assert.Equal(90, packet[DamageType.Lightning].Min);
        Assert.Equal(90, packet[DamageType.Lightning].Max);
    }

}
