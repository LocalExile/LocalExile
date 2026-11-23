using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    sealed class StatEquipment(IReadOnlyList<(byte Row, InventoryItem Item)> items) : IReadOnlyList<(byte Row, InventoryItem Item)>
    {
        public int Count => items.Count;
        public (byte Row, InventoryItem Item) this[int index] => items[index];
        public IEnumerator<(byte Row, InventoryItem Item)> GetEnumerator() => items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Complete item inputs for stat calculation. Additional sources never become socket or skill hosts.</summary>
    public static IReadOnlyList<(byte Row, InventoryItem Item)> EquipmentForStats(Player player,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment = null)
    {
        equipment ??= ItemsOf(player).EquipmentSnapshot();
        if (equipment is StatEquipment) return equipment;
        if (!GameEvents.ItemStatSources.Any) return equipment;
        var result = new List<(byte Row, InventoryItem Item)>(equipment);
        var ids = equipment.Select(e => e.Item.Id).ToHashSet();
        foreach (var (owner, run) in GameEvents.ItemStatSources.Subscribers(player))
            GameEventOwners.Guard(owner, nameof(GameEvents.ItemStatSources), () =>
            {
                foreach (var entry in run(player, equipment))
                    if (ItemModStats.EquipmentRows.Contains(entry.Row) && ids.Add(entry.Item.Id)) result.Add(entry);
            });
        return new StatEquipment(result);
    }
}
