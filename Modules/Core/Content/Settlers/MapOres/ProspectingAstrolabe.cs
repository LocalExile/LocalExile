using GameServer.Modding;

namespace GameServer.Modules.Settlers.MapOres;

/// <summary>
/// Settlers-side support for the <c>Prospecting Astrolabe</c> (<c>Metadata/Items/Currency/AstrolabeSettlers</c>,
/// <c>BaseItemTypes 5392</c>, <c>AtlasMissionTypes</c> row 10):
/// <list type="bullet">
/// <item>Atlas node application (<c>C-&gt;S 0x0075 / 0x0076</c>), Shaped Region persistence, <c>S-&gt;C 0x01a3</c>
/// section <c>0x10</c> wire sync, map stat injection (<c>MapSpawnSettlersOre</c>,
/// <c>ProspectingAstrolabeEnabledMap</c>, <c>AstrolabeEnabledMap</c>), and Memory Vault completion unlocks are owned
/// by <c>Modules/Core/World/Atlas/AstrolabesAndVaults.cs</c>.</item>
/// <item>This hook additionally allows placing a Prospecting Astrolabe directly in a Map Device slot to inject the
/// same Prospecting Astrolabe stats into the opened map, while <see cref="MapOreEncounter"/> and
/// <see cref="MapOreStats"/> react to <c>Stat.ProspectingAstrolabeEnabledMap</c> from either source.</item>
/// </list>
/// </summary>
public sealed class ProspectingAstrolabe
{
    public bool MapDeviceAccepts(Player? player, uint typeHash, string itemClass) =>
        Items.ByHash(typeHash)?.Id.Equals(MapOreTables.ProspectingAstrolabeId, StringComparison.Ordinal) == true;

    /// <summary>
    /// <see cref="GameEvents.MapOpening"/> hook: enables Prospecting Astrolabe stats when a Prospecting Astrolabe
    /// is placed directly into a Map Device slot. Maps inside an Atlas Shaped Region receive the same stats from
    /// <c>Atlas/AstrolabesAndVaults</c> via <c>AtlasMissionTypes</c> row 10.
    /// </summary>
    public void OnMapOpening(MapOpening request)
    {
        if (request.Opening is not { } opening || request.Campaign) return;

        bool directSlot = request.Slots.Any(s => !s.IsMap && s.BaseId.Equals(MapOreTables.ProspectingAstrolabeId, StringComparison.Ordinal));
        if (!directSlot) return;

        var mods = opening.Mods
            .WithFragmentStat(Stat.ProspectingAstrolabeEnabledMap, 1)
            .WithFragmentStat(Stat.AstrolabeEnabledMap, 1)
            .WithFragmentStat(Stat.MapSpawnSettlersOre, 1);

        request.Opening = opening with { Mods = mods };
        request.HandledContent.Add(MapScarabContent.KalguuranOre);
    }
}
