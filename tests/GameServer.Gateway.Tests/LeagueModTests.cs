using GameServer.Core.Accounts;

namespace GameServer.Gateway.Tests;

/// <summary>A mod of the leagues that list it, and the leagues and world data a mod brings (Agents/docs/dev/modding.md,
/// "Leagues").</summary>
public sealed class LeagueModTests
{
    [Fact]
    public void A_league_is_made_of_modules_and_the_mods_it_lists()
    {
        string root = Fixture();
        try
        {
            string mods = Path.Combine(root, "mods");
            // The mod that is off (by its mod.json, or the state file) gives nothing.
            Assert.Equal(["Shared", "every", "listed", "rival", "solo", "unlisted"], ModFolders.On(mods).Select(Path.GetFileName));
            Assert.Equal([Path.Combine(mods, "listed", "Leagues")], ModFolders.Roots("Leagues", mods));
            string[] modules = [Path.Combine(root, "Modules")];
            var catalog = LeagueCatalog.FromFolder(Path.Combine(root, "Leagues"), null, modules, ModFolders.On(mods));

            // A mod's league is a league like the repository's own: its mods are in its one list, after its modules.
            Assert.Equal(["Standard", "Shared"], catalog.Loaded.Select(l => l.Name));
            Assert.Equal(["Core/Game", "listed"], catalog.FindLoaded("Shared")!.Modules);
            Assert.Contains("listed", ServerLeagues.Wanted(catalog));
            // The repository's Standard stays. A league does not load without a mod it names, with a module its mod cannot
            // run with, or with a mod beside one whose "only" does not name it.
            Assert.Equal(
            [
                "Clash: listed cannot run with Rival",
                "NeedsAbsent: it needs the mod absent, which is not installed or is turned off",
                "NeedsOff: it needs the mod off, which is not installed or is turned off",
                "Only: solo allows only the modules and mods it names, not listed",
                "Standard: a league of that name is already loaded",
            ], catalog.Errors.Order());

            // The user's own lists take a mod as they take a module; a change that breaks a limit is an error line.
            var errors = new List<string>();
            var limits = ModFolders.Limits(modules, errors, ModFolders.On(mods));
            var changed = LeagueCatalog.ChangeModules([.. catalog.Loaded],
                """{ "Shared": { "add": ["Core/Rival"] }, "Standard": { "add": ["unlisted"] } }""", errors, limits);
            Assert.Equal(["Core/Game", "unlisted"], changed[0].Modules);
            Assert.Equal(["Core/Game", "listed"], changed[1].Modules);
            Assert.Equal(["league-modules.json: Shared: listed cannot run with Rival"], errors);
            changed = LeagueCatalog.ChangeModules(changed, """{ "Shared": { "remove": ["LISTED"], "add": ["Core/Rival"] } }""", errors, limits);
            Assert.Equal(["Core/Game", "Core/Rival"], changed[1].Modules);
            Assert.Single(errors);
            // Shared no longer needs the mod its user removed; as it ships, it does.
            var none = new HashSet<string>();
            Assert.NotNull(new LeagueCatalog(changed, mods: none).FindLoaded("Shared"));
            Assert.Null(new LeagueCatalog(catalog.Loaded, mods: none).FindLoaded("Shared"));

            // Its world data: the mod that is on brings its update files to the world database, the others none.
            Assert.Equal([Path.Combine(mods, "listed", "sql")], ModFolders.Roots("sql", mods));
            string? folder = Environment.GetEnvironmentVariable("Game_MODS_FOLDER");
            try
            {
                Environment.SetEnvironmentVariable("Game_MODS_FOLDER", mods);
                string world = Path.Combine(root, "world.db");
                using (new GameServer.Storage.GameDataStore(world)) { }
                using var db = GameServer.Storage.SqlUpdater.Open(world);
                using var command = db.CreateCommand();
                command.CommandText = "SELECT group_concat(name) FROM listed_things";
                Assert.Equal("listed", command.ExecuteScalar());
            }
            finally { Environment.SetEnvironmentVariable("Game_MODS_FOLDER", folder); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_listed_mod_loads_for_the_leagues_that_list_it_and_runs_between_the_modules_and_the_other_mods()
    {
        string root = Fixture();
        var lines = new List<string>();
        try
        {
            string mods = Path.Combine(root, "mods");
            using var leagues = new ModHost(Path.Combine(root, "Leagues"), lines.Add, modules: true, leagues: true) { More = () => ModFolders.Roots("Leagues", mods) };
            using var modules = new ModHost(Path.Combine(root, "Modules"), lines.Add, modules: true)
            {
                Wanted = () => new HashSet<string>(["Rival"]),
                SettingsFolder = Path.Combine(root, "data", "modules"),
            };
            using var host = new ModHost(mods, lines.Add)
            {
                Modules = modules,
                Leagues = leagues,
                Wanted = () => new HashSet<string>(["Game", "listed"], StringComparer.OrdinalIgnoreCase),
                SettingsFolder = Path.Combine(root, "data", "mods"),
            };
            leagues.Start(watch: false);
            modules.Start(watch: false);
            host.Start(watch: false);
            string log = string.Join("\n", lines);

            // The mod's league loads from its folder. The mod itself loads because a league lists it; its entry class is a
            // module's, and it gives league rules as a module does.
            Assert.True(leagues.IsLoaded("Shared"), log);
            Assert.Contains(lines, l => l.StartsWith("[leagues] Standard: ") && l.Contains("is not loaded"));
            Assert.True(host.IsLoaded("listed"), log);
            Assert.True(host.IsLoaded("every"), log);
            Assert.Equal(7.0, ServerLeagues.Live("listed")!.ItemDropMultiplier);
            Assert.Contains(lines, l => l.StartsWith("[mods] every: refused: only a module, or a mod of the leagues that list it"));
            // No league lists the third; the fourth has a module's id (so its limits are not the module's: the Clash league's
            // error above is the listed mod's); the fifth is off.
            Assert.Contains(host.Mods(), m => m.Id == "unlisted" && m.Error!.StartsWith("no league lists it"));
            Assert.Contains(host.Mods(), m => m.Id == "rival" && m.Error == "LocalExile has a module of that id");
            Assert.Contains(host.Mods(), m => m.Id == "off" && m.Error == "disabled");
            // One named exactly as a league's folder: events rank an owner by its id.
            Assert.Contains(host.Mods(), m => m.Id == "Shared" && m.Error == "a league's folder is named exactly that");
            Assert.True(modules.IsLoaded("Rival"));

            // What its user set while it was a module is its as a mod; a module's own values stay.
            Assert.True(File.Exists(Path.Combine(root, "data", "mods", "listed.json")), log);
            Assert.False(File.Exists(Path.Combine(root, "data", "modules", "Listed.json")));
            Assert.True(File.Exists(Path.Combine(root, "data", "modules", "Rival.json")));
            Assert.True(File.Exists(Path.Combine(root, "data", "modules", "Stated.json")));   // a module no league lists, and a mod of its name

            // A listed mod runs for the leagues that list it, after their modules and before the mods of every league.
            Assert.Equal(GameEventOwners.LeagueModRank, ModHost.RankOf("listed"));
            Assert.Equal(GameEventOwners.ModRank, ModHost.RankOf("every"));
            var hook = new GameEvent<Func<string>>();
            using var every = hook.Add("every", () => "every");
            using var listed = hook.Add("listed", () => "listed");
            using var module = hook.Add("Rival", () => "Rival");
            LeagueRuntime Runtime(params string[] list)
            {
                var league = new League("L", "L", false, false) { Modules = list };
                return new(league, ServerLeagues.ModulesOf(league));
            }
            Assert.Equal(["Rival", "listed", "every"], hook.For(Runtime("Core/Rival", "Listed")).Select(run => run()));
            Assert.Equal(["Rival", "every"], hook.For(Runtime("Core/Rival")).Select(run => run()));
            // A league's own code runs for that league alone, also where another league lists a mod of its name.
            using var code = hook.Add("Shared", () => "Shared");
            Assert.Equal(GameEventOwners.LeagueRank, ModHost.RankOf("Shared"));
            Assert.Equal(["Rival", "every"], hook.For(Runtime("Core/Rival", "shared")).Select(run => run()));
            Assert.Equal(["Shared", "every"], hook.For(new LeagueRuntime(new League("Shared", "Shared", false, false), new HashSet<string>())).Select(run => run()));

            host.Dispose();
            Assert.Null(ServerLeagues.Live("listed"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Leagues/Standard and Modules/Core/Rival as a repository has them, and mods/: one of the leagues that list it, which brings
    // leagues and world data; one of every league; one with "only"; one no league lists; one with a module's id; two turned off.
    static string Fixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "le-league-mods-" + Guid.NewGuid().ToString("N"));
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
        void AddMod(string id, string json, string code = "public sealed class Entry : IMod { public void Load(IModContext mod) { } }")
        {
            Put($"mods/{id}/mod.json", json);
            Put($"mods/{id}/{id}.cs", code);
        }
        AddLeague("Leagues", "Standard", "{}", "Core/Game");
        Put("Modules/Core/Rival/module.json", "{}");
        Put("Modules/Core/Rival/Rival.cs", "public sealed class RivalModule : IModule { public void Load(IModuleContext module) { } }");
        Put("data/modules/Rival.json", "{}");
        Put("data/modules/Listed.json", "{}");
        Put("Modules/Core/Stated/module.json", "{}");
        Put("data/modules/Stated.json", "{}");

        AddMod("listed", """{ "id": "listed", "version": "1.0.0", "leagues": "listed", "conflicts": ["Rival"] }""",
            "public sealed class Entry : IModule { public void Load(IModuleContext module) => module.ProvideLeagueRules(() => LeagueRules.Standard with { ItemDropMultiplier = 7 }); }");
        AddLeague("mods/listed/Leagues", "Shared", """{ "parent": "Standard", "mods": ["listed"] }""", "Core/Game");
        AddLeague("mods/listed/Leagues", "Standard", "{}", "Core/Game");
        AddLeague("mods/listed/Leagues", "Clash", """{ "parent": "Standard", "mods": ["listed"] }""", "Core/Game", "Core/Rival");
        AddLeague("mods/listed/Leagues", "Only", """{ "parent": "Standard", "mods": ["solo", "listed"] }""", "Core/Game");
        AddLeague("mods/listed/Leagues", "NeedsAbsent", """{ "parent": "Standard", "mods": ["absent"] }""", "Core/Game");
        AddLeague("mods/listed/Leagues", "NeedsOff", """{ "parent": "Standard", "mods": ["off"] }""", "Core/Game");
        Put("mods/listed/sql/updates/world/2026_01_01_000000_listed.sql", "CREATE TABLE listed_things (name TEXT NOT NULL) STRICT; INSERT INTO listed_things VALUES ('listed');");

        AddMod("every", """{ "id": "every" }""",
            "public sealed class Entry : IMod { public void Load(IModContext mod) { try { mod.ProvideLeagueRules(() => LeagueRules.Standard); } catch (InvalidOperationException e) { mod.Log(\"refused: \" + e.Message); } } }");
        AddMod("solo", """{ "id": "solo", "leagues": "listed", "only": [] }""");
        AddMod("unlisted", """{ "id": "unlisted", "leagues": "listed" }""");
        AddMod("rival", """{ "id": "rival", "only": [] }""");

        AddMod("Shared", """{ "id": "Shared" }""");
        AddMod("off", """{ "id": "off", "Enabled": false }""");
        AddLeague("mods/off/Leagues", "Hidden", """{ "parent": "Standard" }""", "Core/Game");
        Put("mods/off/sql/updates/world/2026_01_01_000000_off.sql", "CREATE TABLE off_things (name TEXT NOT NULL) STRICT;");
        AddMod("stated", """{ "id": "stated" }""");
        AddLeague("mods/stated/Leagues", "Stated", """{ "parent": "Standard" }""", "Core/Game");
        Put("mods/stated/sql/updates/world/2026_01_01_000000_stated.sql", "INSERT INTO listed_things VALUES ('stated');");
        Put("mods/mod-state.txt", "stated off\n");
        return root;
    }
}
