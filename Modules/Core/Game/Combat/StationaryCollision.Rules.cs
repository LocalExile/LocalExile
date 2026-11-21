using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The area's stationary objects (the stash, a bench, doodads, quest objects) block every player's walk; collision is
    // each player's own, so a player who enters later is given them too (EnterAreaFixtures).
    sealed class AreaFixtures
    {
        public uint Stash, GuildStash, Bench;
        public (int X, int Y) StashAt, GuildStashAt, BenchAt;
        public readonly Dictionary<uint, Func<Player, Task>> Collisions = [];
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameArea, AreaFixtures> Fixtures = new();
    static AreaFixtures FixturesOf(Player player) => Fixtures.GetValue(player.Area, _ => new());

    /// <summary>An area object's collision: every player in the area now, and each one who enters later.</summary>
    public static async Task RegisterStationaryCollision(Player player, uint id, string type, int x, int y,
        float rotation, string? editorKey = null, string? model = null)
    {
        Task Register(Player p) => RegisterPlayerCollision(p, id, type, x, y, rotation, editorKey, model);
        // An object of a player's own view blocks only them.
        if (Program.ViewOf.Value is { } viewer) { await Register(viewer); return; }
        FixturesOf(player).Collisions[id] = Register;
        foreach (var p in player.Frames.Players().Where(p => !p.Closed && !p.LeavingArea).Append(player).Distinct()) await Register(p);
    }

    /// <summary>A player enters: the area's stash, guild stash and bench are theirs to open, and its objects block them.</summary>
    public static async Task EnterAreaFixtures(Player player)
    {
        var fixtures = FixturesOf(player);
        if (fixtures.Stash != 0) (player.StashObject, player.StashAt) = (fixtures.Stash, fixtures.StashAt);
        if (fixtures.GuildStash != 0) (player.GuildStashObject, player.GuildStashAt) = (fixtures.GuildStash, fixtures.GuildStashAt);
        if (fixtures.Bench != 0) (player.CraftingBenchObject, player.CraftingBenchAt) = (fixtures.Bench, fixtures.BenchAt);
        foreach (var (id, register) in fixtures.Collisions.ToArray())
            if (player.Area.Objects.Has(id)) await register(player); else fixtures.Collisions.Remove(id);
    }

    /// <summary>The area's stash, guild stash or bench (what EnterAreaFixtures gives a later player).</summary>
    public static void SetAreaStash(Player player, uint id, (int X, int Y) at) { var f = FixturesOf(player); (f.Stash, f.StashAt) = (id, at); }
    public static void SetAreaGuildStash(Player player, uint id, (int X, int Y) at) { var f = FixturesOf(player); (f.GuildStash, f.GuildStashAt) = (id, at); }
    public static void SetAreaBench(Player player, uint id, (int X, int Y) at) { var f = FixturesOf(player); (f.Bench, f.BenchAt) = (id, at); }

    /// <summary>One player's collision with a stationary object (an NPC of their quest view, or an area object's share).</summary>
    public static async Task RegisterPlayerCollision(Player player, uint id, string type, int x, int y,
        float rotation, string? editorKey = null, string? model = null)
    {
        var (size, hull) = StationaryCollision.Resolve(type, model);
        if (size >= 0) player.SetBlocker(id, x, y, size, hull);
        else player.RemoveBlocker(id);
        if (StationaryCollision.For(type).BlockingLeagueFlag == "HaveGuild"
            && player.Character.LeagueQuestFlags.Add(Quests.League.HaveGuild))
            await SendWorldRecord(player, player.Id, InstanceEntities.PlayerLeagueQuestFlagsUpdate(player.Id,
                Quests.League.Bits(player.Character.LeagueQuestFlags)), "guild storage access enables native collision");
        Console.WriteLine($"[gateway] stationary collision 0x{id:x} {type} at ({x},{y}): size {size}, hull {hull?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");
        if (editorKey is not null) player.HideoutEditor.Register(id, editorKey, x, y, rotation, size, hull);
    }
}
