using System.Runtime.CompilerServices;
using GameServer.Core.Accounts;
using Mod = GameServer.Modding;

// The mods in Mods/Binaries/ (ModHost, Agents/docs/dev/modding.md) and what the Gateway shows them: a player (ModPlayer) and the
// monsters it kills. The hooks are called where the Gateway does the same thing itself (commands, the player tick, the
// message dispatch, the area's setup, a kill, its drops and experience, the character's stats).
partial class Program
{
    // A league (Leagues/<Name>/) is a set of modules (Modules/, Modules/Core/); a mod (Mods/) is a small modification, global and the
    // user's own. Agents/docs/dev/modules.md. The leagues and their module lists are read when the Gateway starts, and do not change
    // while it runs; the code of a loaded league, module or mod hot-reloads.
    internal static string ModulesFolder => Environment.GetEnvironmentVariable("Game_MODULES_FOLDER") ?? "Modules";
    internal static string LeaguesFolder => Environment.GetEnvironmentVariable("Game_LEAGUES_FOLDER") ?? "Leagues";

    internal static readonly ModHost ServerLeagueCode = new(LeaguesFolder, Console.WriteLine, modules: true, leagues: true)
    {
        Active = (player, id) => player.LeagueId.Equals(id, StringComparison.OrdinalIgnoreCase),
        WorldOf = player => Logic.WorldFor(ConnOf(player)),
    };

    internal static readonly ModHost ServerModules = new(ModulesFolder, Console.WriteLine, modules: true)
    {
        Wanted = () => ServerLeagues.Wanted(LeagueCatalog.Startup),
        // A module runs only for the players whose league lists it, so a league without one has nothing of it to break on.
        Active = (player, id) => ModulesFor(ConnOf(player)).Contains(id),
        WorldOf = player => Logic.WorldFor(ConnOf(player)),
    };
    internal static readonly ModHost ServerMods = new(Environment.GetEnvironmentVariable("Game_MODS_FOLDER") ?? "Mods/Binaries", Console.WriteLine);
    internal static readonly ServerHooks ServerHooks = new(ServerLeagueCode, ServerModules, ServerMods);

    internal static void StartMods()
    {
        var leagues = LeagueCatalog.Startup;
        if (leagues.FindLoaded(LeagueCatalog.Standard) is null)
            throw new InvalidOperationException($"The Standard league is required: no {Path.Combine(LeaguesFolder, LeagueCatalog.Standard, ModHost.LeagueManifestFile)}");
        foreach (string error in leagues.Errors) Console.WriteLine($"[leagues] not loaded: {error}");
        foreach (var league in leagues.Loaded)
            foreach (string path in league.Modules)
                if (!File.Exists(Path.Combine(ModulesFolder, path, ModHost.ModuleManifestFile)))
                    Console.WriteLine($"[leagues] {league.Name}'s modules.json lists {path}: no such module in {ModulesFolder}/");
        Console.WriteLine($"[leagues] loaded: {string.Join(", ", leagues.Loaded.Select(l => l.Name + (l.Ended ? " (ended)" : "")))}");
        StaleModules.Rebuild(ModulesFolder);
        ModLoadContext.DeleteOldCopies();
        ServerLeagueCode.Changed += line => HotReload.Tell(line.Replace("[leagues] ", "Leagues: "));
        ServerLeagueCode.Start();
        ServerModules.Changed += line => HotReload.Tell(line.Replace("[modules] ", "Modules: "));
        ServerModules.Start();
        if (Environment.GetEnvironmentVariable("Game_MODS") == "0") { Console.WriteLine("[mods] off (Game_MODS=0)"); return; }
        // A mod (re)loaded or failed while players are in: they read it in chat, as a hot reload.
        ServerMods.Changed += line => HotReload.Tell(line.Replace("[mods] ", "Mods: "));
        ServerMods.Start();
    }

    // /mod_enable and /mod_disable.
    internal static Task SetModEnabled(ClientState conn, string[] args, bool enabled)
    {
        if (args.Length != 1) throw new ArgumentException("which mod? /mod lists them");
        return conn.Commands!.Reply(ServerMods.SetEnabled(args[0], enabled)
            ? $"Mod {args[0]} {(enabled ? "enabled" : "disabled")} (/mod for the list)." : $"No mod \"{args[0]}\" in {ServerMods.Folder}/ (/mod for the list).");
    }

    /// <summary>The character's league: Standard, or its module's (Modding/ServerLeagues.cs), decided once per connection.</summary>
    internal static LeagueRuntime LeagueFor(ClientState conn) =>
        conn.LeagueModule ??= ServerLeagues.Resolve(conn.LeagueForm, conn.League, LeagueCatalog.Startup);

    /// <summary>The modules a connection's league lists.</summary>
    internal static IReadOnlySet<string> ModulesFor(ClientState conn) => LeagueFor(conn).Modules;

    internal static readonly ConditionalWeakTable<ClientState, IConnectionPlayer> ModPlayers = new();

    static IGameLogic? _logic;

    /// <summary>The game's rules, once the game module is loaded. A reload's old rules keep running until the new ones replace
    /// them, so a connection always has some.</summary>
    internal static IGameLogic? GameLogicOrNull => Volatile.Read(ref _logic);
    internal static IGameLogic Logic => GameLogicOrNull ?? throw new InvalidOperationException("the game module is not loaded");

    /// <summary>The game module loaded. What held its old types is let go, so that its load context can be collected.</summary>
    internal static void SetGameLogic(IGameLogic logic)
    {
        Volatile.Write(ref _logic, logic);
        ModPlayers.Clear();
    }

    /// <summary>The player as mods see it (one per connection).</summary>
    internal static Mod.IPlayer PlayerOf(ClientState conn) => ModPlayers.GetValue(conn, Logic.PlayerFor);

    /// <summary>The connection behind a player, for what the Gateway gives modules.</summary>
    internal static ClientState ConnOf(Mod.IPlayer player) => ((IConnectionPlayer)player).Conn;
}
