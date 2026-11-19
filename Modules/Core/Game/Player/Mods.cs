using System.Runtime.CompilerServices;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public static MonsterInfo ModMonster(SpawnedMonster m, (int X, int Y) at) =>
        new(m.Variety?.Id ?? "", m.Variety?.Name ?? m.Label, m.Level,
            m.Rarity, at.X, at.Y) { ObjectId = m.ObjectId };

    // A mod's extra drop (DropRoll.Items), named as /item takes it: a unique's name, else a BaseItemTypes id or name.
    public static ItemDrops.Drop? ModDrop(Player conn, string query, int itemLevel)
    {
        if (UniquesFor(conn).Find(query) is { } unique
            && UniquesFor(conn).Create(unique, AreaRng(conn)) is { } created && ItemsFor(conn).ByHash(created.TypeHash) is { } uniqueType)
            return new(created, uniqueType);
        if (ItemsFor(conn).Find(query) is not { Supported: true } type) return null;
        if (type.Stackable) return new(ItemCatalog.Create(type), type);
        var (sockets, links) = ItemsFor(conn).DefaultSockets(type, itemLevel);
        return new(ItemCatalog.Create(type, sockets, links) with { ItemLevel = (byte)Math.Clamp(itemLevel, 1, 100) }, type);
    }

    /// <summary>The items the league's and the mods' drop changes add to a kill (DropRoll.Items as /item makes them,
    /// DropRoll.RolledItems as the kill rolls its own), with every DropRoll.Changes applied to all of
    /// <paramref name="drops"/> and them.</summary>
    public static List<ItemDrops.Drop> ModdedDrops(Player conn, DropRoll? modded, List<ItemDrops.Drop> drops, int level,
        MonsterRarity rarity, int increasedRarity, double rarityMultiplier, Random rng)
    {
        if (modded is null) return drops;
        foreach (string query in modded.Items)
            if (ModDrop(conn, query, level) is { } extra) drops.Add(extra);
            else Console.WriteLine($"[mods] a mod's drop \"{query}\" is no item type or unique the server can create");
        foreach (string query in modded.RolledItems)
            if (ItemsFor(conn).Find(query) is { } type
                && DropsFor(conn).RollBase(type, level, rarity, rng, increasedRarity, rarityMultiplier * modded.Rarity) is { } rolled)
                drops.Add(rolled);
            else Console.WriteLine($"[mods] a rolled drop \"{query}\" is no item type the server can create");
        return [.. modded.Changed(drops, rng)];
    }

    public sealed class ModPlayer(Player conn) : IConnectionPlayer
    {
        public Player Conn => conn;
        public string Character => CharacterName(conn);
        public string? Account => conn.Account;
        public string League => conn.League ?? "Standard";
        public bool Hardcore => conn.Hardcore;
        public bool SoloSelfFound => conn.Ssf;
        public string LeagueId => LeagueFor(conn).Id;
        public bool Uses(string module) => LeagueFor(conn).Modules.Contains(module);
        public int Level => conn.Pools.Level;
        public string AreaId => conn.Layout.Id;
        public uint Seed => conn.Seed;
        public bool Closed => conn.Closed;

        public (int X, int Y) Position
        {
            get { lock (conn.Monsters) return conn.Walk.Position(AreaTime(conn)); }
        }

        public Task Tell(string text) => conn.Commands is { } commands && !conn.Closed ? commands.Reply(text) : Task.CompletedTask;

        public Task Send(byte[] message, string? log = null) =>
            conn.Outbox is { } outbox && !conn.Closed ? outbox(message, log) : Task.CompletedTask;
    }
}
