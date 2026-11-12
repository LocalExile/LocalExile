using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The nine ailment and avoidance tattoo rows of issue #372: the client's PassiveSkillOverrides carry each
// replacement passive's exact stats. No engine change is needed for them: the Panels stage publishes each display
// total with its base_* sources summed in (StatSnapshotCache), so the existing ailment chance, avoidance, stun
// recovery and bleed readers already see the replacement stats.
public sealed class TattooAilmentTests
{
    // The nine catalogue rows and the stats/values the client's PassiveSkillOverrides rows carry.
    static readonly (string Name, string Stat, int Value)[] Rows =
    [
        ("Tattoo of the Arohongui Scout", "base_avoid_chill_%", 10),
        ("Tattoo of the Arohongui Scout", "base_avoid_freeze_%", 10),
        ("Tattoo of the Arohongui Warmonger", "base_chance_to_freeze_%", 5),
        ("Tattoo of the Kitava Rebel", "base_avoid_bleed_%", 10),
        ("Tattoo of the Kitava Shaman", "bleed_on_hit_with_attacks_%", 5),
        ("Tattoo of the Ngamahu Warmonger", "base_chance_to_ignite_%", 5),
        ("Tattoo of the Tasalio Tideshifter", "base_avoid_stun_%", 10),
        ("Tattoo of the Tawhoa Scout", "base_avoid_poison_%", 10),
        ("Tattoo of the Tawhoa Shaman", "base_chance_to_poison_on_hit_%", 5),
        ("Tattoo of the Valako Shaman", "base_chance_to_shock_%", 5),
    ];

    [Fact]
    public void Every_ailment_and_avoidance_tattoo_maps_its_client_stats()
    {
        foreach (string name in Rows.Select(row => row.Name).Distinct())
        {
            var stats = TattooStats.For(name);
            Assert.NotNull(stats);
            foreach (var (_, stat, value) in Rows.Where(row => row.Name == name))
                Assert.Contains((Program.Stats.Key(stat)!.Value, value), stats);
        }
        // The applied rows are replaced passives, so the catalogue lookup is what 0x33 and the stat pass use.
        Assert.All(Rows.Select(row => row.Name).Distinct(), name => Assert.NotNull(TattooCatalogue.Find(name)));
        // The client's aliases the consumers read already include their base_* sources: the published total is the
        // sum of the display stat and the sources, so the replacement stat is read once.
        Assert.Equal(15, PanelStats.Of("avoid_stun_%", name => name switch
        {
            "avoid_stun_%" => 5, "base_avoid_stun_%" => 10, _ => 0,
        }));
    }
}
