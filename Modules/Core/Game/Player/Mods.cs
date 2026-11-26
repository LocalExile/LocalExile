using System.Runtime.CompilerServices;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public static MonsterInfo ModMonster(SpawnedMonster m, (int X, int Y) at) =>
        new(m.Variety?.Id ?? "", m.Variety?.Name ?? m.Label, m.Level,
            m.Rarity, at.X, at.Y) { ObjectId = m.ObjectId };

    // A mod's stacks (DropRoll.Stacks): each stackable item type with its count, split at the type's stack size.
    public static IEnumerable<ItemDrops.Drop> ModStacks(Player player, DropRoll? modded)
    {
        if (modded is null || modded.Stacks.Count == 0) yield break;
        foreach (var (query, count) in modded.Stacks)
        {
            if (count <= 0) continue;
            if (ItemsFor(player).Find(query) is not { Supported: true, Stackable: true } type)
            {
                Console.WriteLine($"[mods] a mod's stack \"{query}\" is no stackable item type the server can create");
                continue;
            }
            for (int left = count; left > 0; left -= type.MaxStack)
                yield return new(ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(left, type.MaxStack) }, type);
        }
    }

    // A mod's extra drop (DropRoll.Items), named as /item takes it: a unique's name, else a BaseItemTypes id or name.
    public static ItemDrops.Drop? ModDrop(Player player, string query, int itemLevel)
    {
        if (UniquesFor(player).Find(query) is { } unique
            && UniquesFor(player).Create(unique, AreaRng(player)) is { } created && ItemsFor(player).ByHash(created.TypeHash) is { } uniqueType)
            return new(created, uniqueType);
        if (ItemsFor(player).Find(query) is not { Supported: true } type) return null;
        if (type.Stackable) return new(ItemCatalog.Create(type), type);
        var (sockets, links) = ItemsFor(player).DefaultSockets(type, itemLevel);
        return new(ItemCatalog.Create(type, sockets, links) with { ItemLevel = (byte)Math.Clamp(itemLevel, 1, 100) }, type);
    }

    // A mod's ready-made drops (DropRoll.Made); one whose base type the server does not know is left out.
    public static IEnumerable<ItemDrops.Drop> MadeDrops(Player player, DropRoll? modded)
    {
        foreach (var item in modded?.Made ?? [])
            if (ItemsFor(player).ByHash(item.TypeHash) is { } type) yield return new(item, type);
            else Console.WriteLine($"[mods] a mod's drop 0x{item.TypeHash:x8} is no item type the server knows");
    }

    // Marks every corruptible item in a monster's drop list as corrupted (DropRoll.Corrupted): equipment, jewels, gems and maps.
    public static void CorruptDrops(List<ItemDrops.Drop> drops) => CorruptDrops(drops, 100);

    static void CorruptDrops(List<ItemDrops.Drop> drops, int chance, Random? rng = null)
    {
        for (int i = 0; i < drops.Count; i++)
        {
            var d = drops[i];
            if (d.Item.Corrupted || d.Item.Mirrored || d.Item.Vestigial || d.Type.Stackable || d.Type.Unmodifiable
                || d.Type.Components.Contains("Charges") || d.Type.Components.Contains("Quest")
                || d.Type.Components.Contains("DeepwaterChart") || d.Type.Components.Contains("HeistBlueprint")) continue;
            if ((d.Type.CanBeCorrupted || d.Type.Components.Contains("Mods")
                || d.Type.Components.Contains("SkillGem") || d.Type.Components.Contains("Map")
                || d.Type.Components.Contains("MapKey"))
                && (chance >= 100 || rng is not null && rng.Next(100) < chance))
                drops[i] = d with { Item = d.Item with { Corrupted = true } };
        }
    }

    /// <summary>The items the league's and the mods' drop changes add to a kill (DropRoll.Items as /item makes them,
    /// DropRoll.RolledItems as the kill rolls its own), with every DropRoll.Changes applied to all of
    /// <paramref name="drops"/> and them.</summary>
    public static List<ItemDrops.Drop> ModdedDrops(Player player, DropRoll? modded, List<ItemDrops.Drop> drops, int level,
        MonsterRarity rarity, int increasedRarity, double rarityMultiplier, Random rng)
    {
        if (modded is null) return drops;
        foreach (string query in modded.Items)
            if (ModDrop(player, query, level) is { } extra) drops.Add(extra);
            else Console.WriteLine($"[mods] a mod's drop \"{query}\" is no item type or unique the server can create");
        foreach (string query in modded.RolledItems)
            if (ItemsFor(player).Find(query) is { } type
                && DropsFor(player).RollBase(type, level, rarity, rng, increasedRarity, rarityMultiplier * modded.Rarity) is { } rolled)
                drops.Add(rolled);
            else Console.WriteLine($"[mods] a rolled drop \"{query}\" is no item type the server can create");
        return [.. modded.Changed(drops, rng)];
    }

    public sealed class ModPlayer(Player player) : IGamePlayer
    {
        public Player Player => player;
        public string Character => CharacterName(player);
        public string? Account => player.Character.Account;
        public string League => player.Character.League ?? "Standard";
        public bool Hardcore => player.Character.Hardcore;
        public bool SoloSelfFound => player.Character.Ssf;
        public string LeagueId => LeagueFor(player).Id;
        public bool Uses(string module) => LeagueFor(player).Modules.Contains(module);
        public int Level => player.Pools.Level;
        public string AreaId => player.Layout.Id;
        public uint Seed => player.Seed;
        public bool Closed => player.Closed;

        public (int X, int Y) Position
        {
            get { return player.Walk.Position(AreaTime(player)); }
        }

        public Task Tell(string text) => player.Commands is { } commands && !player.Closed ? commands.Reply(text) : Task.CompletedTask;

        public Task Send(byte[] message, string? log = null) =>
            player.Connection.Outbox is { } outbox && !player.Closed ? outbox(message, log) : Task.CompletedTask;
    }
}
