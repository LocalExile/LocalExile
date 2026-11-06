using GameServer.Core.Accounts;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class LeagueReloadRulesTests
{
    [Fact]
    public void League_code_replacement_keeps_the_latest_rules_without_changing_module_fallback()
    {
        const string name = "reload-rules-test", module = "reload-module-test";
        var definition = new GameServer.Core.Accounts.League(name, name, false, false) { Parent = "Standard", Modules = [module] };
        var catalog = new LeagueCatalog([new("Standard", "Standard", false, false), definition]);
        Assert.Empty(catalog.Errors);
        var runtime = ServerLeagues.Resolve(null, name, catalog);
        var modules = ServerLeagues.ModulesOf(definition);
        var code = new MutableLeague { Current = LeagueRules.Standard with { GoldMultiplier = 7 } };
        ServerLeagues.AddRules(module, () => LeagueRules.Standard with { GoldMultiplier = 2 });
        try
        {
            ServerLeagues.SetCode(name, code);
            Assert.Equal(7, runtime.Rules.GoldMultiplier);
            code.Current = code.Current! with { GoldMultiplier = 9 };
            ServerLeagues.RemoveCode(name);
            Assert.Equal(9, runtime.Rules.GoldMultiplier);
            Assert.Equal(9, ServerLeagues.Resolve(null, name, catalog).Rules.GoldMultiplier);
            ServerLeagues.RemoveCode(name); // Repeated retirement preserves the snapshot.
            Assert.Equal(9, ServerLeagues.RulesOf(definition, modules).GoldMultiplier);
            ServerLeagues.SetCode(name, new MutableLeague { Current = LeagueRules.Standard with { GoldMultiplier = 3 } });
            Assert.Equal(3, runtime.Rules.GoldMultiplier);
            ServerLeagues.SetCode(name, new MutableLeague()); // Null deliberately restores the module's policy.
            Assert.Equal(2, runtime.Rules.GoldMultiplier);
            ServerLeagues.RemoveCode(name);
            Assert.Equal(2, runtime.Rules.GoldMultiplier);
            ServerLeagues.RemoveRules(module);
            Assert.Equal(1, runtime.Rules.GoldMultiplier);
        }
        finally
        {
            ServerLeagues.SetCode(name, new MutableLeague());
            ServerLeagues.RemoveCode(name);
            ServerLeagues.RemoveRules(module);
        }
    }

    sealed class MutableLeague : GameServer.Modding.League
    {
        public LeagueRules? Current { get; set; }
        public override LeagueRules? Rules => Current;
    }
}
