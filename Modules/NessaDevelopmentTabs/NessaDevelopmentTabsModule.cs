using GameServer.Modding;

namespace GameServer.Modules.NessaDevelopmentTabs;

/// <summary>Nessa's development tabs (Awakened Gems, Exceptional Gems, Maps, Originator Maps, Unique Maps) for the leagues
/// that list this module. Its <c>mode</c> setting: <c>add</c> puts them after her progression tabs, <c>replace</c> shows the
/// full development layout instead (<see cref="LeagueRules.FullGemStock"/>). The rules are Standard's with these: a league
/// takes the rules of one module, so this one does not share a league with Ruthless.</summary>
public sealed class NessaDevelopmentTabsModule : IModule
{
    sealed record Choices(string Mode);

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        // settings.json allows only add and replace.
        string mode = module.Settings<Choices>().Mode;
        var rules = Rules(mode)!;
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
