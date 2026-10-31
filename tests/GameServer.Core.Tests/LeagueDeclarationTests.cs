using GameServer.Core.Accounts;

namespace GameServer.Core.Tests;

public sealed class LeagueDeclarationTests : IDisposable
{
    readonly string _mods = Path.Combine(Path.GetTempPath(), "localexile-leagues-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_mods)) Directory.Delete(_mods, recursive: true); }

    void Mod(string id, string json)
    {
        Directory.CreateDirectory(Path.Combine(_mods, id));
        File.WriteAllText(Path.Combine(_mods, id, "module.json"), json);
    }

    const string Form = """
        { "league": { "form": {
            "standardNames": { "plain": "Grim", "hardcore": "Hardcore Grim", "ssf": "SSF Grim", "hardcoreSsf": "SSF Hardcore Grim" },
            "challengeNames": { "plain": "Grim {base}", "hardcore": "HC Grim {base}", "ssf": "SSF Grim {base}", "hardcoreSsf": "HC SSF G {base}" },
            "description": "Grim.", "permanentDeath": true } } }
        """;

    [Fact]
    public void Standard_is_the_only_league_without_a_module()
    {
        var catalog = new LeagueCatalog([LeagueCatalog.Standard]);
        Assert.Equal(["Standard", "Hardcore", "SSF Standard", "SSF Hardcore"], catalog.All.Select(l => l.Name));
    }

    [Fact]
    public void A_module_declares_a_base_league_and_a_form_with_its_variants()
    {
        Mod("necro", """{ "league": { "bases": ["Necropolis"] } }""");
        Mod("grim", Form);
        Mod("feature", "{ }");
        var declared = LeagueCatalog.Declarations(_mods);
        var catalog = new LeagueCatalog(declared.SelectMany(d => d.Bases).Prepend(LeagueCatalog.Standard), declared.Select(d => d.Form).OfType<LeagueForm>());
        var grim = catalog.Find("HC SSF G Necropolis")!;
        Assert.Equal(("grim", "Necropolis", true, true, true), (grim.Form, grim.Base, grim.Hardcore, grim.Ssf, grim.PermanentDeath));
        Assert.Equal("HC SSF Necropolis", grim.Entry().RuthlessParent);
        Assert.Equal("Standard", catalog.Find("Grim")!.Base);
        Assert.Contains("Grim.", catalog.Find("Grim")!.Description);
    }

    [Fact]
    public void A_disabled_module_is_not_offered_but_its_characters_keep_their_league()
    {
        Mod("grim", Form);
        File.WriteAllText(Path.Combine(_mods, "module-state.txt"), "grim off\n");
        var declared = Assert.Single(LeagueCatalog.Declarations(_mods));
        Assert.False(declared.Enabled);
        var catalog = new LeagueCatalog([LeagueCatalog.Standard], savedForms: [declared.Form!]);
        Assert.Null(catalog.Find("Hardcore Grim"));
        var saved = catalog.ResolveSavedLeague("Hardcore Grim")!;
        Assert.Equal(("grim", true), (saved.Form, saved.Hardcore));
    }

    [Fact]
    public void A_league_no_module_declares_keeps_its_name_on_standard()
    {
        var saved = new LeagueCatalog([LeagueCatalog.Standard]).ResolveSavedLeague("Gone")!;
        Assert.Equal(("Gone", "Standard", null, false), (saved.Name, saved.Base, saved.Form, saved.Hardcore));
        Assert.Null(new LeagueCatalog([LeagueCatalog.Standard]).ResolveSavedLeague(null));
    }
}
