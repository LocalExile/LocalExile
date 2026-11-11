using System.Text.Json.Nodes;
using GameServer.Core.Accounts;

namespace GameServer.Gateway.Tests;

public sealed class ModSettingsTests
{
    sealed record Bots(int MaxBots, int[] Quest, string Mode, string[] Rarities, Dictionary<string, double> Scale, Spawn Spawning);
    sealed record Spawn(int Every, bool Night);

    [Fact]
    public void User_values_are_checked_over_the_declared_defaults_and_saved_as_what_changed()
    {
        const string Declared = """
        {
          "version": 2,
          "renamed": { "bots": "maxBots" },
          "settings": {
            "maxBots": { "type": "integer", "default": 100, "min": 0, "max": 500 },
            "quest": { "type": "range", "of": "integer", "default": [2, 6], "min": 1, "max": 60 },
            "mode": { "type": "choice", "default": "add", "options": [{ "value": "add", "label": "After" }, "replace"] },
            "rarities": { "type": "choices", "default": ["unique"], "source": "rarity", "minItems": 1 },
            "scale": { "type": "map", "default": {}, "options": ["normal", "magic"], "value": { "type": "number", "min": 0 } },
            "spawning": { "type": "group", "settings": {
              "every": { "type": "integer", "default": 60, "min": 1, "format": "duration", "unit": "s" },
              "night": { "type": "bool", "default": false, "when": { "maxBots": 100 } } } }
          }
        }
        """;
        IEnumerable<string>? Rarity(string kind) => kind == "rarity" ? ["normal", "magic", "rare", "unique"] : null;
        var schema = ModSettings.Parse(Declared, Rarity);
        var log = new List<string>();

        // A renamed key moves, a value out of its limits keeps the default, a group takes its own values, an old key stays.
        var merged = ModSettings.Merge(schema, """
            { "bots": 40, "quest": [9, 3], "mode": "replace", "rarities": ["rare", "UNIQUE"], "scale": { "magic": 2.5 },
              "spawning": { "every": 0, "night": true }, "gone": 1 }
            """, log.Add, Rarity);
        var bots = ModSettings.Bound<Bots>(merged);
        Assert.Equal(40, bots.MaxBots);
        Assert.Equal([2, 6], bots.Quest);
        Assert.Equal("replace", bots.Mode);
        Assert.Equal(["rare", "UNIQUE"], bots.Rarities);
        Assert.Equal(2.5, bots.Scale["magic"]);
        Assert.Equal(new Spawn(60, true), bots.Spawning);
        Assert.Contains(log, l => l.Contains("quest has its low above its high"));
        Assert.Contains(log, l => l.Contains("spawning.every is below 1"));
        Assert.Contains(log, l => l.Contains("gone is no longer used"));
        Assert.Contains("is not a known rarity", ModSettings.Check(schema.Settings["rarities"], JsonNode.Parse("""["boss"]"""), Rarity));
        Assert.Contains("is not one of its keys", ModSettings.Check(schema.Settings["scale"], JsonNode.Parse("""{ "rare": 1 }""")));

        // Saving writes what differs from the defaults, with the names as declared, and keeps what is no longer declared.
        var saved = JsonNode.Parse(ModSettings.Saved(schema, bots with { Mode = "add" }, """{ "gone": 1 }""", Rarity))!.AsObject();
        Assert.Equal(["gone", "maxBots", "rarities", "scale", "spawning"], saved.Select(p => p.Key).Order());
        Assert.True((bool)saved["spawning"]!["night"]!);
        Assert.Throws<ArgumentException>(() => ModSettings.Saved(schema, bots with { MaxBots = 501 }, null, Rarity));

        // A wrong declaration fails the load, its default included; every shipped settings.json reads (the modules', and
        // those of mods installed from source: the mods live in LocalExile-Mods, so a checkout may have no Mods/Source).
        Assert.Throws<FormatException>(() => ModSettings.Parse("""{ "settings": { "x": { "type": "integer", "default": 1.5 } } }"""));
        Assert.Throws<FormatException>(() => ModSettings.Parse("""{ "settings": { "x": { "type": "choice", "default": "a" } } }"""));
        Assert.Throws<FormatException>(() => ModSettings.Parse("""{ "settings": { "x": { "type": "bool", "default": true, "colour": 1 } } }"""));
        string modSources = Path.Combine("Mods", "Source");
        var shipped = Directory.GetFiles("Modules", ModSettings.FileName, SearchOption.AllDirectories)
            .Concat(Directory.Exists(modSources) ? Directory.GetFiles(modSources, ModSettings.FileName, SearchOption.AllDirectories) : [])
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToList();
        Assert.NotEmpty(shipped);
        foreach (string file in shipped) ModSettings.Parse(File.ReadAllText(file));
    }

    [Fact]
    public void A_users_module_changes_add_and_remove_over_the_leagues_own_lists()
    {
        List<League> leagues =
        [
            new("Standard", "Standard", false, false) { Modules = ["Core/Game", "Core/Item/Tattoos", "Core/NPC/Vendors"] },
            new("Ruthless", "Ruthless", false, false) { Modules = ["Core/Game", "Ruthless"] },
        ];
        var errors = new List<string>();
        var changed = LeagueCatalog.ChangeModules(leagues, """
            {
              "standard": { "add": ["PlayerBots", "NessaDevelopmentTabs", "Core/NPC/Vendors"], "remove": ["Tattoos", "Core/Game"] },
              "Ruthless": { "add": "PlayerBots" },
              "Nowhere": { "add": ["PlayerBots"] }
            }
            """, errors);

        // Added modules go last, an id removes its path, the game stays, and the leagues' own lists are untouched.
        Assert.Equal(["Core/Game", "Core/NPC/Vendors", "PlayerBots", "NessaDevelopmentTabs"], changed[0].Modules);
        Assert.Equal(["Core/Game", "Ruthless"], changed[1].Modules);
        Assert.Equal(["Core/Game", "Core/Item/Tattoos", "Core/NPC/Vendors"], leagues[0].Modules);
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("cannot remove the game itself"));
        Assert.Contains(errors, e => e.Contains("Ruthless's changes are not"));
        Assert.Contains(errors, e => e.Contains("no league \"Nowhere\""));

        // An unreadable file changes no league.
        Assert.Same(leagues, LeagueCatalog.ChangeModules(leagues, "{ not json", errors));
        Assert.Equal(4, errors.Count);
    }
}
