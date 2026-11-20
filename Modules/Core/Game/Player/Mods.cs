using System.Runtime.CompilerServices;
using GameServer.Core.Accounts;

public static partial class Game
{
    public static MonsterInfo ModMonster(SpawnedMonster m, (int X, int Y) at) =>
        new(m.Variety?.Id ?? "", m.Variety?.Name ?? m.Label, m.Level,
            m.Rarity, at.X, at.Y) { ObjectId = m.ObjectId };

    // A mod's stacks (DropRoll.Stacks): each stackable item type with its count, split at the type's stack size.
    public static IEnumerable<ItemDrops.Drop> ModStacks(ClientState conn, DropRoll? modded)
    {
        if (modded is null || modded.Stacks.Count == 0) yield break;
        foreach (var (query, count) in modded.Stacks)
        {
            if (count <= 0) continue;
            if (ItemsFor(conn).Find(query) is not { Supported: true, Stackable: true } type)
            {
                Console.WriteLine($"[mods] a mod's stack \"{query}\" is no stackable item type the server can create");
                continue;
            }
            for (int left = count; left > 0; left -= type.MaxStack)
                yield return new(ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(left, type.MaxStack) }, type);
        }
    }

    // A mod's extra drop (DropRoll.Items), named as /item takes it: a unique's name, else a BaseItemTypes id or name.
    public static ItemDrops.Drop? ModDrop(ClientState conn, string query, int itemLevel)
    {
        if (UniquesFor(conn).Find(query) is { } unique
            && UniquesFor(conn).Create(unique, Random.Shared) is { } created && ItemsFor(conn).ByHash(created.TypeHash) is { } uniqueType)
            return new(created, uniqueType);
        if (ItemsFor(conn).Find(query) is not { Supported: true } type) return null;
        if (type.Stackable) return new(ItemCatalog.Create(type), type);
        var (sockets, links) = ItemsFor(conn).DefaultSockets(type, itemLevel);
        return new(ItemCatalog.Create(type, sockets, links) with { ItemLevel = (byte)Math.Clamp(itemLevel, 1, 100) }, type);
    }

    // A mod's ready-made drops (DropRoll.Made); one whose base type the server does not know is left out.
    public static IEnumerable<ItemDrops.Drop> MadeDrops(ClientState conn, DropRoll? modded)
    {
        foreach (var item in modded?.Made ?? [])
            if (ItemsFor(conn).ByHash(item.TypeHash) is { } type) yield return new(item, type);
            else Console.WriteLine($"[mods] a mod's drop 0x{item.TypeHash:x8} is no item type the server knows");
    }

    // Marks every corruptible item in a monster's drop list as corrupted (DropRoll.Corrupted): equipment, jewels, gems and maps.
    public static void CorruptDrops(List<ItemDrops.Drop> drops)
    {
        for (int i = 0; i < drops.Count; i++)
        {
            var d = drops[i];
            if (d.Item.Corrupted || d.Item.Mirrored || d.Type.Stackable || d.Type.Unmodifiable
                || d.Type.Components.Contains("Charges") || d.Type.Components.Contains("Quest")) continue;
            if (d.Type.CanBeCorrupted || d.Type.Components.Contains("Mods")
                || d.Type.Components.Contains("SkillGem") || d.Type.Components.Contains("Map")
                || d.Type.Components.Contains("MapKey"))
                drops[i] = d with { Item = d.Item with { Corrupted = true } };
        }
    }

    public sealed class ModPlayer(ClientState conn) : IConnectionPlayer
    {
        public ClientState Conn => conn;
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
            get { lock (conn.Monsters) return conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds); }
        }

        public Task Tell(string text) => conn.Commands is { } commands && !conn.Closed ? commands.Reply(text) : Task.CompletedTask;

        public Task Send(byte[] message, string? log = null) =>
            conn.Outbox is { } outbox && !conn.Closed ? outbox(message, log) : Task.CompletedTask;
    }
}
