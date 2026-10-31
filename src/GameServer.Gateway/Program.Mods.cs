using System.Runtime.CompilerServices;
using Mod = GameServer.Modding;

// The mods in Mods/Binaries/ (ModHost, Agents/docs/dev/modding.md) and what the Gateway shows them: a player (ModPlayer) and the
// monsters it kills. The hooks are called where the Gateway does the same thing itself (commands, the player tick, the
// message dispatch, the area's setup, a kill, its drops and experience, the character's stats).
partial class Program
{
    // Modules (Agents/docs/dev/modules.md) are overhauls, mods small modifications: two folders, each with its own commands.
    // Game_MODULES=0 and Game_MODS=0 load none from their folder; Game_MODULES_FOLDER and Game_MODS_FOLDER move it (relative
    // to the repository root, like data/). The server's own features behind the API (Features/) are always modules.
    internal static readonly ModHost ServerModules = new ModHost(Environment.GetEnvironmentVariable("Game_MODULES_FOLDER") ?? "Modules/Binaries", Console.WriteLine, modules: true)
        .AddBuiltIn(HarvestFeature.Id, () => new HarvestFeature())
        .AddBuiltIn(HeistFeature.Id, () => new HeistFeature())
        .AddBuiltIn("delve", () => new DelveFeature());
    internal static readonly ModHost ServerMods = new(Environment.GetEnvironmentVariable("Game_MODS_FOLDER") ?? "Mods/Binaries", Console.WriteLine);
    internal static readonly ServerHooks ServerHooks = new(ServerModules, ServerMods);

    static void StartMods()
    {
        if (Environment.GetEnvironmentVariable("Game_MODULES") == "0") Console.WriteLine("[modules] only the built-in ones (Game_MODULES=0)");
        else
        {
            ServerModules.Changed += line => HotReload.Tell(line.Replace("[modules] ", "Modules: "));
            ServerModules.Start();
        }
        if (Environment.GetEnvironmentVariable("Game_MODS") == "0") { Console.WriteLine("[mods] off (Game_MODS=0)"); return; }
        // A mod (re)loaded or failed while players are in: they read it in chat, as a hot reload.
        ServerMods.Changed += line => HotReload.Tell(line.Replace("[mods] ", "Mods: "));
        ServerMods.Start();
    }

    // /module_enable and /module_disable. A league that is turned off leaves the create screen at the next login.
    static Task SetModuleEnabled(ClientState conn, string[] args, bool enabled)
    {
        if (args.Length != 1) throw new ArgumentException("which module? /module lists them");
        return conn.Commands!.Reply(ServerModules.SetEnabled(args[0], enabled)
            ? $"Module {args[0]} {(enabled ? "enabled" : "disabled")} (/module for the list)." : $"No module \"{args[0]}\" in {ServerModules.Folder}/ (/module for the list).");
    }

    // /mod_enable and /mod_disable.
    static Task SetModEnabled(ClientState conn, string[] args, bool enabled)
    {
        if (args.Length != 1) throw new ArgumentException("which mod? /mod lists them");
        return conn.Commands!.Reply(ServerMods.SetEnabled(args[0], enabled)
            ? $"Mod {args[0]} {(enabled ? "enabled" : "disabled")} (/mod for the list)." : $"No mod \"{args[0]}\" in {ServerMods.Folder}/ (/mod for the list).");
    }

    static readonly ConditionalWeakTable<ClientState, ModPlayer> ModPlayers = new();

    /// <summary>The player as mods see it (one per connection).</summary>
    internal static Mod.IPlayer PlayerOf(ClientState conn) => ModPlayers.GetValue(conn, c => new ModPlayer(c));

    /// <summary>The connection behind a player, for the server's own features (Features/).</summary>
    internal static ClientState ConnOf(Mod.IPlayer player) => ((ModPlayer)player).Conn;

    static Mod.MonsterInfo ModMonster(SpawnedMonster m, (int X, int Y) at) =>
        new(m.Variety?.Id ?? "", m.Variety?.Name ?? m.Label, m.Level,
            Enum.TryParse<Mod.MonsterRarity>(m.Rarity.ToString(), out var rarity) ? rarity : Mod.MonsterRarity.Normal, at.X, at.Y);

    // A mod's extra drop (DropRoll.Items), named as /item takes it: a unique's name, else a BaseItemTypes id or name.
    static ItemDrops.Drop? ModDrop(ClientState conn, string query, int itemLevel)
    {
        if (UniquesFor(conn).Find(query) is { } unique
            && UniquesFor(conn).Create(unique.Name, Random.Shared) is { } created && ItemsFor(conn).ByHash(created.TypeHash) is { } uniqueType)
            return new(created, uniqueType);
        if (ItemsFor(conn).Find(query) is not { Supported: true } type) return null;
        if (type.Stackable) return new(ItemCatalog.Create(type), type);
        var (sockets, links) = ItemsFor(conn).DefaultSockets(type, itemLevel);
        return new(ItemCatalog.Create(type, sockets, links) with { ItemLevel = (byte)Math.Clamp(itemLevel, 1, 100) }, type);
    }

    sealed class ModPlayer(ClientState conn) : Mod.IPlayer
    {
        public ClientState Conn => conn;
        public string Character => CharacterName(conn);
        public string? Account => conn.Account;
        public string League => conn.League ?? "Standard";
        public bool Hardcore => conn.Hardcore;
        public bool SoloSelfFound => conn.Ssf;
        public string LeagueId => LeagueFor(conn).Id;
        public int Level => conn.Pools.Level;
        public string AreaId => conn.Layout.Id;
        public uint Seed => conn.Seed;
        public bool Closed => conn.Closed;

        public (int X, int Y) Position
        {
            get { lock (conn.Monsters) return conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds); }
        }

        public Task Tell(string text) => conn.Commands is { } commands && !conn.Closed ? commands.Reply(text) : Task.CompletedTask;

        public Task Send(byte[] message, string? log = null) =>
            conn.Outbox is { } outbox && !conn.Closed ? outbox(message, log) : Task.CompletedTask;
    }
}
