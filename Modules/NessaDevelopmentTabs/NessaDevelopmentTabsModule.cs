using System.Text.Json;
using GameServer.Modding;

namespace GameServer.Modules.NessaDevelopmentTabs;

/// <summary>Nessa's development tabs (Awakened Gems, Exceptional Gems, Maps, Originator Maps, Unique Maps) for the leagues
/// that list this module. <c>settings.json</c>'s <c>mode</c>: <c>add</c> puts them after her progression tabs, <c>replace</c>
/// shows the full development layout instead (<see cref="LeagueRules.FullGemStock"/>). An edit applies at the next
/// <c>/module_reload NessaDevelopmentTabs</c>. The rules are Standard's with these: a league takes the rules of one module,
/// so this one does not share a league with Ruthless.</summary>
public sealed class NessaDevelopmentTabsModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        string path = Path.Combine(module.Folder, "settings.json"), mode = "add";
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(path));
            mode = settings.RootElement.GetProperty("mode").GetString() ?? mode;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException)
        {
            module.Log($"{path}: {e.Message}; mode add");
        }
        if (Rules(mode) is not { } rules)
        {
            module.Log($"mode \"{mode}\" is not add or replace; mode add");
            (mode, rules) = ("add", Rules("add")!);
        }
        module.Log($"mode {mode}");
        module.ProvideLeagueRules(() => rules);
    }

    /// <summary>Standard's rules with Nessa's development tabs added or in place of her progression tabs; null for another mode.</summary>
    public static LeagueRules? Rules(string mode) => mode.ToLowerInvariant() switch
    {
        "add" => LeagueRules.Standard with { DevelopmentShopTabs = true },
        "replace" => LeagueRules.Standard with { FullGemStock = true, SellsMaps = true },
        _ => null,
    };
}
