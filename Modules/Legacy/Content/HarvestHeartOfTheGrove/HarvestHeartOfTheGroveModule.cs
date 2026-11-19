using GameServer.Modding;
using GameServer.Modules.Harvest;

namespace GameServer.Modules.HarvestHeartOfTheGrove;

/// <summary>
/// The Heart of the Grove (3.13 only; 3.14 removed it): on rare occasions a map's Sacred Grove has no crops and Oshabi's
/// encounter instead, a soul tree in the grove. The chance is the chancePercent setting [G: the wiki gives none] times
/// 1 + map_harvest_chance_for_oshabi_+% / 100 (no 3.29 table grants that stat; it is read so that any source applies).
/// The tree stands at the grove's centre [G]. The rewards are Oshabi's 3.29 ones (Core Harvest's world.db rows), not
/// 3.13's, and there is no Groveseed Altar (bloodlines are 3.29).
/// </summary>
public sealed class HarvestHeartOfTheGroveModule : IModule
{
    const string ChanceStat = "map_harvest_chance_for_oshabi_+%";

    sealed record Choices
    {
        public double ChancePercent { get; init; } = 2;
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        double basePercent = module.Settings<Choices>().ChancePercent;
        string id = module.Manifest.Id;
        module.Lifetime.Own(SacredGrove.AddReplacement(new(id, Roll: (conn, _, random) =>
        {
            if (!LeagueFor(conn).Modules.Contains(id)) return false;
            int increase = MapModsOf(conn)?.Get(ChanceStat) ?? 0;
            double chance = basePercent * Math.Max(0, 1 + increase / 100.0);
            if (random.NextDouble() * 100 >= chance) return false;
            module.Log($"{conn.Layout.Id}: the Sacred Grove is the Heart of the Grove (chance {chance:0.##}%: {basePercent:0.##}% "
                + $"x (1 + {increase}%))");
            return true;
        },
        // A reload places it again: the encounter's own saved state keeps its progress.
        Place: (conn, site) => OshabiEncounter.Place(conn, site.Centre, site.Level,
            new(Altar: false, Log: "Heart of the Grove")))));
    }
}
