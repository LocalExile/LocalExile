using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// The corpse-consumption and Hinder tattoo rows of issue #372: their client stats, the spell-only Hinder roll with the
// client's four seconds and the additive Maim/Hinder walk reduction, and the killing blow's corpse consumption (which
// recovers 10% of maximum life).
public sealed class TattooCorpseHinderTests
{
    static readonly (string Name, string Stat, int Value)[] Rows =
    [
        ("Tattoo of the Kitava Heart Eater", "killing_blow_consumes_corpse_restore_10%_life_chance_%", 4),
        ("Tattoo of the Tasalio Shaman", "spells_chance_to_hinder_on_hit_%", 5),
    ];

    [Fact]
    public void Every_corpse_and_hinder_tattoo_maps_its_client_stats()
    {
        foreach (var (name, stat, value) in Rows)
        {
            var stats = TattooStats.For(name);
            Assert.NotNull(stats);
            Assert.Contains((Program.Stats.Key(stat)!.Value, value), stats);
        }
    }
}
