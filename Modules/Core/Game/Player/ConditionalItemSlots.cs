using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public sealed record ConditionalRingSlots(Item? Left, Item? Right)
    {
        public IReadOnlyDictionary<string, bool> Flags => new Dictionary<string, bool>
        {
            ["_if_left_ring_is_magic_item"] = Left?.Rarity == ItemRarity.Magic,
            ["_if_right_ring_is_magic_item"] = Right?.Rarity == ItemRarity.Magic,
        };

        public int? Value(Item source, string key)
        {
            if (key == "left_ring_is_magic_item") return Left?.Rarity == ItemRarity.Magic ? 1 : 0;
            if (key == "right_ring_is_magic_item") return Right?.Rarity == ItemRarity.Magic ? 1 : 0;
            if (key is not ("other_ring_is_shaper_item" or "other_ring_is_elder_item")) return null;
            // The counterpart belongs to the physical source. Its own influence and other equipment cannot satisfy it.
            Item? other;
            if (ReferenceEquals(source, Left)) other = Right;
            else if (ReferenceEquals(source, Right)) other = Left;
            else return null;
            var influence = key == "other_ring_is_shaper_item" ? ItemInfluence.Shaper : ItemInfluence.Elder;
            return other is not null && (other.Influences & influence) != 0 ? 1 : 0;
        }
    }

    public static ConditionalRingSlots ConditionalRings(IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        IReadOnlyCollection<Item>? active = null)
    {
        Item? At(byte row) => equipment.FirstOrDefault(e => e.Row == row
            && (active is null || active.Any(item => ReferenceEquals(item, e.Item.Item)))).Item?.Item;
        return new(At(6), At(7));
    }
}
