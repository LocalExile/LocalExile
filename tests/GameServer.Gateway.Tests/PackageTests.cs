using GameServer.Core.Accounts;

namespace GameServer.Gateway.Tests;

/// <summary>A package: a mod's folder that carries leagues and modules (Agents/docs/dev/modding.md, "Packages").</summary>
public sealed class PackageTests
{
    [Fact]
    public void A_packages_leagues_join_the_catalog_and_keep_their_modules_limits()
    {
        string root = Fixture();
        try
        {
            string mods = Path.Combine(root, "mods");
            // The package that is off (by its mod.json, or the state file) gives nothing.
            Assert.Equal([Path.Combine(mods, "pack", "Leagues")], Packages.Roots("Leagues", mods));
            string[] modules = [Path.Combine(root, "Modules"), .. Packages.Roots("Modules", mods)];
            var catalog = LeagueCatalog.FromFolder(Path.Combine(root, "Leagues"), null, Packages.Roots("Leagues", mods), modules);

            // Its league is a league like the repository's own, with the mods it takes.
            Assert.Equal(["Standard", "Shared"], catalog.Loaded.Select(l => l.Name));
            Assert.Equal(["pack"], catalog.FindLoaded("Shared")!.Mods);
            Assert.Null(catalog.FindLoaded("Standard")!.Mods);
            // The repository's Standard stays; modules that conflict, and a mode with a module it does not name, make no league.
            Assert.Equal(3, catalog.Errors.Count);
            Assert.Contains(catalog.Errors, e => e == "Standard: a league of that name is already loaded");
            Assert.Contains(catalog.Errors, e => e == "Clash: PackModule cannot run with Rival");
            Assert.Contains(catalog.Errors, e => e == "Mode: Solo is a mode and does not allow PackModule");

            // The user's own change that breaks a limit is an error line and changes nothing.
            var errors = new List<string>();
            var limits = Packages.Limits(modules, errors);
            var changed = LeagueCatalog.ChangeModules([.. catalog.Loaded], """{ "Shared": { "add": ["Rival"] }, "Standard": { "add": ["Rival"] } }""", errors, limits);
            Assert.Equal(["Core/Game", "PackModule"], changed[1].Modules);
            Assert.Equal(["Core/Game", "Rival"], changed[0].Modules);
            Assert.Equal(["league-modules.json: Shared: PackModule cannot run with Rival"], errors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_packages_leagues_and_modules_load_beside_the_repositorys_and_a_league_chooses_its_mods()
    {
        string root = Fixture();
        var lines = new List<string>();
        try
        {
            string mods = Path.Combine(root, "mods");
            using var leagues = new ModHost(Path.Combine(root, "Leagues"), lines.Add, modules: true, leagues: true) { More = () => Packages.Roots("Leagues", mods) };
            using var modules = new ModHost(Path.Combine(root, "Modules"), lines.Add, modules: true)
            {
                More = () => Packages.Roots("Modules", mods),
                Wanted = () => new HashSet<string>(["PackModule", "Rival"]),
            };
            using var host = new ModHost(mods, lines.Add) { Modules = modules, Leagues = leagues };
            leagues.Start(watch: false);
            modules.Start(watch: false);
            host.Start(watch: false);

            // The package's league and module load from its folder; the package itself is a mod with nothing of its own to run.
            Assert.True(leagues.IsLoaded("Shared"), string.Join("\n", lines));
            Assert.True(modules.IsLoaded("PackModule"), string.Join("\n", lines));
            Assert.True(host.IsLoaded("pack"), string.Join("\n", lines));
            Assert.False(host.IsLoaded("off"));
            Assert.Contains("PackModule", modules.Ids());
            // An id is one folder: the repository's module stays, and the package's second one is said.
            Assert.True(modules.IsLoaded("Rival"));
            Assert.Contains(lines, l => l.StartsWith("[modules] Rival: ") && l.Contains("is not loaded") && l.Contains(Path.Combine("pack", "Modules", "Rival")));
            Assert.Contains(lines, l => l.StartsWith("[leagues] Standard: ") && l.Contains("is not loaded"));

            // A mod runs in every league, unless the league's "mods" says which it takes.
            var hook = new GameEvent<Action>();
            using var subscription = hook.Add("pack", () => { });
            LeagueRuntime Runtime(IReadOnlyList<string>? takes) => new(new League("L", "L", false, false) { Mods = takes }, new HashSet<string>());
            Assert.Single(hook.For(Runtime(null)));
            Assert.Single(hook.For(Runtime(["Pack"])));
            Assert.Empty(hook.For(Runtime(["other"])));
            Assert.Empty(hook.For(Runtime([])));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Leagues/Standard and Modules/Rival as a repository has them, and mods/ with a package (and one turned off).
    static string Fixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "le-packages-" + Guid.NewGuid().ToString("N"));
        void Put(string path, string text)
        {
            string file = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        void AddLeague(string folder, string name, string json, params string[] modules)
        {
            Put($"{folder}/{name}/league.json", json);
            Put($"{folder}/{name}/modules.json", $$"""{ "modules": [{{string.Join(", ", modules.Select(m => $"\"{m}\""))}}] }""");
            Put($"{folder}/{name}/Source/{name}League.cs", $"public sealed class {name}League : League {{ }}");
        }
        void AddModule(string folder, string id, string json)
        {
            Put($"{folder}/{id}/module.json", json);
            Put($"{folder}/{id}/{id}.cs", $"public sealed class {id}Module : IModule {{ public void Load(IModuleContext module) {{ }} }}");
        }
        AddLeague("Leagues", "Standard", "{}", "Core/Game");
        AddModule("Modules/Core", "Rival", "{}");

        Put("mods/pack/mod.json", """{ "id": "pack", "version": "1.0.0", "kind": "league" }""");
        AddLeague("mods/pack/Leagues", "Shared", """{ "parent": "Standard", "mods": ["pack"] }""", "Core/Game", "PackModule");
        AddLeague("mods/pack/Leagues", "Standard", "{}", "Core/Game");
        AddLeague("mods/pack/Leagues", "Clash", """{ "parent": "Standard" }""", "Core/Game", "PackModule", "Core/Rival");
        AddLeague("mods/pack/Leagues", "Mode", """{ "parent": "Standard" }""", "Core/Game", "Solo", "PackModule");
        AddModule("mods/pack/Modules/Any/Depth", "PackModule", """{ "conflicts": ["Rival"] }""");
        AddModule("mods/pack/Modules", "Rival", "{}");
        AddModule("mods/pack/Modules", "Solo", """{ "only": [] }""");

        Put("mods/off/mod.json", """{ "id": "off", "kind": "league", "enabled": false }""");
        AddLeague("mods/off/Leagues", "Hidden", """{ "parent": "Standard" }""", "Core/Game");
        Put("mods/stated/mod.json", """{ "id": "stated", "kind": "league" }""");
        AddLeague("mods/stated/Leagues", "Stated", """{ "parent": "Standard" }""", "Core/Game");
        Put("mods/mod-state.txt", "stated off\n");
        return root;
    }
}
