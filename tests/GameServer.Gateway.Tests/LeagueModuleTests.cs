using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class LeagueModuleTests
{
    sealed class CustomLeague(string name, params string[] features) : ILeague
    {
        public string Id => name.ToLowerInvariant();
        public string ClientLeagueName => name;
        public LeagueRules Rules { get; set; } = new() { GoldMultiplier = 3 };
        public IReadOnlyList<string> RequiredFeatures => features;
    }

    [Fact]
    public void Ruthless_characters_get_the_module_with_its_data_and_rules()
    {
        var league = ServerLeagues.Resolve(ruthless: true, "SSF Hardcore Ruthless", _ => true);
        Assert.Equal(("ruthless", true), (league.Id, league.HardMode));
        Assert.True(league.Rules.PersistsVitals);
        Assert.False(league.Rules.AllowsGemBase("Metadata/Items/Gems/SkillGemFrostblink"));
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("Hardcore")]
    [InlineData("Allflame")]
    [InlineData(null)]
    public void Other_leagues_without_a_module_are_standard(string? name) =>
        Assert.Same(LeagueRuntime.Standard, ServerLeagues.Resolve(false, name, _ => true));

    [Fact]
    public void A_custom_league_takes_its_name_until_it_unloads_then_its_characters_are_standard()
    {
        var custom = new CustomLeague("Testlands");
        Assert.True(ServerLeagues.Add(custom));
        Assert.False(ServerLeagues.Add(new CustomLeague("Testlands")));
        try
        {
            var league = ServerLeagues.Resolve(false, "Testlands", _ => true);
            Assert.Equal(("testlands", 3d), (league.Id, league.Rules.GoldMultiplier));
            custom.Rules = new() { GoldMultiplier = 5 };
            Assert.Equal(5, league.Rules.GoldMultiplier);   // tunables apply at once
            ServerLeagues.Remove(custom);
            Assert.Equal(5, league.Rules.GoldMultiplier);   // the connection keeps its rules until its next area
            Assert.Same(LeagueRuntime.Standard, ServerLeagues.Resolve(false, "Testlands", _ => true));
        }
        finally { ServerLeagues.Remove(custom); }
    }

    [Fact]
    public void A_league_whose_feature_is_not_loaded_is_standard()
    {
        var custom = new CustomLeague("Needy", "harvest");
        Assert.True(ServerLeagues.Add(custom));
        try
        {
            string? logged = null;
            Assert.Same(LeagueRuntime.Standard, ServerLeagues.Resolve(false, "Needy", _ => false, line => logged = line));
            Assert.Contains("needs harvest", logged);
            Assert.Equal("needy", ServerLeagues.Resolve(false, "Needy", id => id == "harvest").Id);
        }
        finally { ServerLeagues.Remove(custom); }
    }

    [Fact]
    public void A_ruthless_character_without_the_module_plays_by_standard_rules_on_a_connection()
    {
        var conn = new ClientState(new(ushort.MaxValue, "LeagueTest", 128, 128, 32, 32, null, []), 0, 1) { Ruthless = true, LeagueModule = LeagueRuntime.Standard };
        Assert.False(Program.LeagueFor(conn).HardMode);
        Assert.Same(Program.Items, Program.ItemsFor(conn));
    }
}
