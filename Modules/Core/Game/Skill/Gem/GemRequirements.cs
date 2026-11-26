using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public sealed record GemRequirementSnapshot(int Level, int Strength, int Dexterity, int Intelligence,
        IReadOnlyList<(byte Row, InventoryItem Item)> Equipment, IReadOnlySet<uint> Inactive)
    {
        public bool IgnoreAttributeRequirements { get; init; }

        public IReadOnlyList<(byte Row, uint ItemId, int Socket, Item Gem)> Places => PlacesIn(Equipment);

        public static IReadOnlyList<(byte Row, uint ItemId, int Socket, Item Gem)> PlacesIn(
            IReadOnlyList<(byte Row, InventoryItem Item)> equipment) =>
            [.. equipment.OrderBy(e => e.Row).SelectMany(e => e.Item.Item.Sockets.Select((s, socket) =>
                (e.Row, e.Item.Id, Socket: socket, s.Colour, s.Gem)).Where(g => g.Colour != SocketColour.Abyss && g.Gem is not null)
                .Select(g => (g.Row, g.Id, g.Socket, g.Gem!)))];
    }

    // Same supported attribute calculation as pool construction, from a single current equipment read.
    // No PoolsFor/SupportedSkill recursion, pool assignment/refill or old-snapshot cache publication.
    public static GemRequirementSnapshot CurrentGemRequirements(Player player,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment = null)
    {
        equipment ??= ItemsOf(player).EquipmentSnapshot();
        var (sources, attributes, inactive, _) = EquipmentStatSources(player, equipment);
        var cls = SkillsFor(player).ClassFor(player.Character.ClassRow);
        int god = player.God ? 1000 : 0;
        var (str, dex, intel) = attributes.Apply(cls.Strength + god, cls.Dexterity + god, cls.Intelligence + god);
        return new(player.Pools.Level, RequirementAttribute(player, Stat.Strength, str),
            RequirementAttribute(player, Stat.Dexterity, dex),
            RequirementAttribute(player, Stat.Intelligence, intel), equipment, inactive)
        {
            IgnoreAttributeRequirements = sources.GetValueOrDefault(Stat.KeystoneSupremeProdigy) != 0
        };
    }

    static bool SocketedGemAttributeRequirementsIgnored(ItemCatalog catalog, Item host) =>
        ItemModStats.LocalValue(host, catalog.Mods, Stat.LocalGemsSocketedHaveNoAttributeRequirements) != 0;

    static InventoryItem? GemRequirementHost(Player player, Item gem, GemRequirementSnapshot? snapshot, uint? targetId)
    {
        var equipment = snapshot?.Equipment ?? ItemsOf(player).EquipmentSnapshot();
        foreach (var (_, host) in equipment)
            if (targetId is uint id ? host.Id == id
                : host.Item.Sockets.Any(socket => socket.Colour != SocketColour.Abyss && ReferenceEquals(socket.Gem, gem)))
                return host;
        return null;
    }
}
