using System.Runtime.CompilerServices;
using GameServer.Core.Accounts;

// The hosts of leagues' code, modules and mods (ModHost, Agents/docs/dev/modding.md), and the player as GameServer.Modding shows it.
// What they hook into is GameEvents (GameEvents.cs): one hook system, each subscriber running for its league's players.
public partial class Program
{
    // A league (Leagues/<Name>/) is a set of modules (Modules/Core/) and of the mods it lists; a mod (Mods/) is
    // the user's opt-in content, in every league or in the leagues that list it. Agents/docs/dev/modules.md. The leagues and their module lists are read when the Gateway starts, and do not change
    // while it runs; the code of a loaded league, module or mod hot-reloads.
    public static string ModulesFolder => Environment.GetEnvironmentVariable("Game_MODULES_FOLDER") ?? "Modules";
    public static string LeaguesFolder => Environment.GetEnvironmentVariable("Game_LEAGUES_FOLDER") ?? "Leagues";

    // A mod's Leagues/ (Core's ModFolders) adds its leagues to the repository's.
    public static readonly ModHost ServerLeagueCode = new(LeaguesFolder, Console.WriteLine, modules: true, leagues: true)
    {
        More = () => ModFolders.Roots("Leagues"),
        GameValues = GameValuesOf,
    };

    public static readonly ModHost ServerModules = new(ModulesFolder, Console.WriteLine, modules: true)
    {
        Wanted = () => ServerLeagues.Wanted(LeagueCatalog.Startup),
        GameValues = GameValuesOf,
    };
    // A mod gets what a module gets, in every league or (mod.json "leagues": "listed") in the leagues that list it: the
    // server's assemblies, the modules it depends on, the player's world.
    public static readonly ModHost ServerMods = new(ModFolders.Folder, Console.WriteLine)
    {
        Wanted = () => ServerLeagues.Wanted(LeagueCatalog.Startup),
        Modules = ServerModules,
        Leagues = ServerLeagueCode,
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
        var known = ServerModules.Ids().Concat(ModFolders.On().Select(m => Path.GetFileName(m)!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var league in leagues.Loaded)
            foreach (string path in league.Modules)
                if (!known.Contains(ModFolders.Id(path)))
                    Console.WriteLine($"[leagues] {league.Name} lists {path}: no such module in {ModulesFolder}/, and no such mod that is on");
        Console.WriteLine($"[leagues] loaded: {string.Join(", ", leagues.Loaded.Select(l => l.Name + (l.Ended ? " (ended)" : "")))}");
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
    public static Task SetModEnabled(Player player, string[] args, bool enabled)
    {
        if (args.Length != 1) throw new ArgumentException("which mod? /mod lists them");
        return player.Commands!.Reply(ServerMods.SetEnabled(args[0], enabled)
            ? $"Mod {args[0]} {(enabled ? "enabled" : "disabled")} (/mod for the list)." : $"No mod \"{args[0]}\" in {ServerMods.Folder}/ (/mod for the list).");
    }

    /// <summary>The character's league: Standard, or its module's (Modding/ServerLeagues.cs), decided once per connection.</summary>
    public static LeagueRuntime LeagueFor(Player player) =>
        player.LeagueModule ??= ServerLeagues.Resolve(player.Character.LeagueForm, player.Character.League, LeagueCatalog.Startup);

    /// <summary>The modules a connection's league lists.</summary>
    public static IReadOnlySet<string> ModulesFor(Player player) => LeagueFor(player).Modules;

    public static readonly ConditionalWeakTable<Player, IGamePlayer> ModPlayers = new();

    static IGameLogic? _logic;
    static readonly Lock PlayerUpdatesGate = new();
    static readonly ConditionalWeakTable<Player, object> ActivePlayerUpdates = new();

    // Only admitted areas register. The registry holds Gateway identities, never old game callbacks.
    public static void ActivatePlayerUpdates(Player player)
    {
        IGameLogic? logic;
        lock (PlayerUpdatesGate)
        {
            ActivePlayerUpdates.GetValue(player, _ => new object());
            logic = GameLogicOrNull;
        }
        logic?.ResumePlayerUpdates(player);
    }

    /// <summary>The game's rules, once the game module is loaded. A reload's old rules keep running until the new ones replace
    /// them, so a connection always has some.</summary>
    public static IGameLogic? GameLogicOrNull => Volatile.Read(ref _logic);
    public static IGameLogic Logic => GameLogicOrNull ?? throw new InvalidOperationException("the game module is not loaded");

    /// <summary>The game module loaded. What held its old types is let go, so that its load context can be collected.</summary>
    public static void SetGameLogic(IGameLogic logic)
    {
        Player[] active;
        lock (PlayerUpdatesGate)
        {
            Volatile.Write(ref _logic, logic);
            active = [.. ActivePlayerUpdates.Select(p => p.Key)];
        }
        ModPlayers.Clear();
        foreach (var player in active)
        {
            if (player.Closed || player.RageRetired) { ActivePlayerUpdates.Remove(player); continue; }
            logic?.ResumePlayerUpdates(player);
        }
    }

    /// <summary>The player as mods see it (one per connection).</summary>
    /// <summary>What a module or mod can do with this player's world: objects, walking, windows, crafting and saved data.</summary>
    public static IPlayerWorld WorldOf(IPlayer player) => Logic.WorldFor(PlayerOf(player));

    /// <summary>What a module or mod can do with this connection's world.</summary>
    public static IPlayerWorld WorldOf(Player player) => Logic.WorldFor(player);

    public static IPlayer PlayerOf(Player player) =>
        ModPlayers.TryGetValue(player, out var modPlayer) ? modPlayer : ModPlayers.GetValue(player, Logic.PlayerFor);

    /// <summary>The connection behind a player, for what the Gateway gives modules.</summary>
    public static Player PlayerOf(IPlayer player) => ((IGamePlayer)player).Player;
}
