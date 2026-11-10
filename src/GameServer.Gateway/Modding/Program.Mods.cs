using System.Runtime.CompilerServices;
using GameServer.Core.Accounts;

// The hosts of leagues' code, modules and mods (ModHost, Agents/docs/dev/modding.md), and the player as GameServer.Modding shows it.
// What they hook into is GameEvents (GameEvents.cs): one hook system, each subscriber running for its league's players.
public partial class Program
{
    // A league (Leagues/<Name>/) is a set of modules (Modules/Core/, Legacy/, Custom/); a mod (Mods/) is the user's opt-in custom
    // content, in every league. Agents/docs/dev/modules.md. The leagues and their module lists are read when the Gateway starts, and do not change
    // while it runs; the code of a loaded league, module or mod hot-reloads.
    public static string ModulesFolder => Environment.GetEnvironmentVariable("Game_MODULES_FOLDER") ?? "Modules";
    public static string LeaguesFolder => Environment.GetEnvironmentVariable("Game_LEAGUES_FOLDER") ?? "Leagues";

    public static readonly ModHost ServerLeagueCode = new(LeaguesFolder, Console.WriteLine, modules: true, leagues: true)
    {
        GameValues = GameValuesOf,
    };

    public static readonly ModHost ServerModules = new(ModulesFolder, Console.WriteLine, modules: true)
    {
        Wanted = () => ServerLeagues.Wanted(LeagueCatalog.Startup),
        GameValues = GameValuesOf,
    };
    // A mod gets what a module gets, in every league: the server's assemblies, the modules it depends on, the player's world.
    public static readonly ModHost ServerMods = new(Environment.GetEnvironmentVariable("Game_MODS_FOLDER") ?? "Mods/Binaries", Console.WriteLine)
    {
        Modules = ServerModules,
        GameValues = GameValuesOf,
    };

    // A setting's source (an area, an item, ...): what /goto and the other commands complete; unknown before the game module loads.
    static IEnumerable<string>? GameValuesOf(string kind) => GameLogicOrNull?.CompletionValues(kind)?.Select(v => v.Value);

    public static void StartMods()
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
        ServerModules.Reloaded += ServerMods.ReloadUsersOf;
        ServerMods.Start();
    }

    // /mod_enable and /mod_disable.
    public static Task SetModEnabled(ClientState conn, string[] args, bool enabled)
    {
        if (args.Length != 1) throw new ArgumentException("which mod? /mod lists them");
        return conn.Commands!.Reply(ServerMods.SetEnabled(args[0], enabled)
            ? $"Mod {args[0]} {(enabled ? "enabled" : "disabled")} (/mod for the list)." : $"No mod \"{args[0]}\" in {ServerMods.Folder}/ (/mod for the list).");
    }

    /// <summary>The character's league: Standard, or its module's (Modding/ServerLeagues.cs), decided once per connection.</summary>
    public static LeagueRuntime LeagueFor(ClientState conn) =>
        conn.LeagueModule ??= ServerLeagues.Resolve(conn.LeagueForm, conn.League, LeagueCatalog.Startup);

    /// <summary>The modules a connection's league lists.</summary>
    public static IReadOnlySet<string> ModulesFor(ClientState conn) => LeagueFor(conn).Modules;

    public static readonly ConditionalWeakTable<ClientState, IConnectionPlayer> ModPlayers = new();

    static IGameLogic? _logic;
    static readonly Lock PlayerUpdatesGate = new();
    static readonly ConditionalWeakTable<ClientState, object> ActivePlayerUpdates = new();

    // Only admitted areas register. The registry holds Gateway identities, never old game callbacks.
    public static void ActivatePlayerUpdates(ClientState conn)
    {
        IGameLogic? logic;
        lock (PlayerUpdatesGate)
        {
            ActivePlayerUpdates.GetValue(conn, _ => new object());
            logic = GameLogicOrNull;
        }
        logic?.ResumePlayerUpdates(conn);
    }

    /// <summary>The game's rules, once the game module is loaded. A reload's old rules keep running until the new ones replace
    /// them, so a connection always has some.</summary>
    public static IGameLogic? GameLogicOrNull => Volatile.Read(ref _logic);
    public static IGameLogic Logic => GameLogicOrNull ?? throw new InvalidOperationException("the game module is not loaded");

    /// <summary>The game module loaded. What held its old types is let go, so that its load context can be collected.</summary>
    public static void SetGameLogic(IGameLogic logic)
    {
        ClientState[] active;
        lock (PlayerUpdatesGate)
        {
            Volatile.Write(ref _logic, logic);
            active = [.. ActivePlayerUpdates.Select(p => p.Key)];
        }
        ModPlayers.Clear();
        foreach (var conn in active)
        {
            if (conn.Closed || conn.RageRetired) { ActivePlayerUpdates.Remove(conn); continue; }
            logic?.ResumePlayerUpdates(conn);
        }
    }

    /// <summary>The player as mods see it (one per connection).</summary>
    /// <summary>What a module or mod can do with this player's world: objects, walking, windows, crafting and saved data.</summary>
    public static IPlayerWorld WorldOf(IPlayer player) => Logic.WorldFor(ConnOf(player));

    /// <summary>What a module or mod can do with this connection's world.</summary>
    public static IPlayerWorld WorldOf(ClientState conn) => Logic.WorldFor(conn);

    public static IPlayer PlayerOf(ClientState conn) => ModPlayers.GetValue(conn, static c => Logic.PlayerFor(c));

    /// <summary>The connection behind a player, for what the Gateway gives modules.</summary>
    public static ClientState ConnOf(IPlayer player) => ((IConnectionPlayer)player).Conn;
}
