using GameServer.Core.Protocol.Instance;

static partial class Game
{
    // Derived from the actual socket contents on every read; never saved into either paid passive budget.
    public static ushort[] ForbiddenPassiveNodes(ClientState conn, IEnumerable<ushort>? ordinary = null, int? level = null)
    {
        var rules = JewelGrantPolicy.Current;
        if (!rules.Enabled) return [];
        lock (conn.Monsters)
        {
            var allocated = (ordinary ?? conn.Passives).ToHashSet();
            var catalog = ItemsFor(conn);
            var active = ItemsOf(conn).Items(PlayerInventories.PassiveJewels).Where(i =>
                (PassivesFor(conn).JewelNodeForSlot(i.X) is { } socket && allocated.Contains(socket)
                    || ClusterSlotOpen(conn, i.X))
                && catalog.ByHash(i.Item.TypeHash) is { Class: "Jewel" } type
                && ItemModStats.LevelRequirement(i.Item, type.DropLevel, catalog.Mods) <= (level ?? conn.Pools.Level));
            IEnumerable<ushort> granted = ForbiddenJewelCatalogue.Granted(active.Select(i => i.Item), catalog.Mods);
            if (!rules.CrossClass)
            {
                var allowed = new AscendancyCatalog().Choices((byte)conn.ClassRow).SelectMany(d => d.Nodes.Keys).ToHashSet();
                granted = granted.Where(allowed.Contains);
            }
            return (rules.MaximumPairs == 0 ? granted : granted.Take(rules.MaximumPairs)).ToArray();
        }
    }

    internal static ushort[] PassiveEffectNodes(ClientState conn)
    {
        lock (conn.Monsters)
            return [.. conn.Passives.Concat(conn.MainAscendancy.Allocated).Concat(ForbiddenPassiveNodes(conn)).Distinct()];
    }
}
