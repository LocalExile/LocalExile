using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;
using System.Reflection;
using DamageType = GameServer.Core.World.DamageType;

namespace GameServer.Gateway.Tests;

// Task #215: the Whisperer ascendancy notable "we know all" (PassiveSkills.dat graph id 22720) grants
// attack_skills_have_added_lightning_damage_equal_to_%_of_maximum_mana (Stats.dat row 11881, client key 11882;
// the node's other stat, base_mana_cost_-%, is a mana-cost change handled elsewhere). Path of Building pinned
// 16de4b82: ModParser.lua lines 5636–5639 map the value to BOTH LightningMin and LightningMax as BASE with PercentStat Mana and the
// Attack flag, so the packet is added lightning of min = max = percent% of the maximum mana pool (invariance
// under spending/reservation, unlike PoE1 logic built on the current pool). Here: max mana 1000, 6% → 60-60,
// not 1-60. The bonus joins the added-lightning range before the increases, so increased/decreased damage and
// later conversion scale it like any other added lightning, and only the client-visible rows round it.
public class ManaAddedLightningTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static Func<string, int> Reader(StatCatalog catalog, Dictionary<uint, int> stats) =>
        name => catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    static DamagePacket Hit(DamagePacket raw, Func<string, int> get, bool spell, bool melee,
        double effectiveness = 1, DamageConversion? conversion = null) =>
        PlayerHitDamage.Resolve(DamageStats.RawAdded(raw, get, spell, effectiveness), get,
            conversion ?? new(), spell, melee);

    static Dictionary<uint, int> With(StatCatalog catalog, (string Name, int Value) tag, int mana)
    {
        var stats = new Dictionary<uint, int> { [catalog.Key("maximum_mana")!.Value] = mana };
        if (tag.Value != 0) stats[catalog.Key(tag.Name)!.Value] = tag.Value;
        return stats;
    }

    [Fact]
    public void The_attack_hit_has_added_lightning_min_and_max_equal_to_percent_of_maximum_mana()
    {
        var catalog = new StatCatalog(Data());
        var get = Reader(catalog, With(catalog, (DamageStats.MaxManaAddedLightning, 6), 1000));

        var packet = Hit(DamagePacket.Of(DamageType.Physical, 10, 20), get, spell: false, melee: false);

        Assert.Equal(10, packet[DamageType.Physical].Min);
        Assert.Equal(20, packet[DamageType.Physical].Max);
        // 6% of 1000 hits both bounds: 60-60, not 1-60.
        Assert.Equal(60, packet[DamageType.Lightning].Min);
        Assert.Equal(60, packet[DamageType.Lightning].Max);
        Assert.Equal(0, packet[DamageType.Fire].Min);
        Assert.Equal(0, packet[DamageType.Cold].Min);
        Assert.Equal(0, packet[DamageType.Chaos].Min);
    }

}
