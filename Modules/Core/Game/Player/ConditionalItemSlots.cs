using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public sealed record ConditionalRingSlots(Item? Left, Item? Right)
    {
        // Extra ring pairs from GameEvents.ItemStatSources: each ring reads the other ring of its own pair.
        public IReadOnlyList<(Item? Left, Item? Right)> Layers { get; init; } = [];
        public IReadOnlyDictionary<string, bool> Flags => new Dictionary<string, bool>
        {
            ["_if_left_ring_is_magic_item"] = Left?.Rarity == ItemRarity.Magic,
            ["_if_right_ring_is_magic_item"] = Right?.Rarity == ItemRarity.Magic,
        };

        public int? Value(Item source, string key)
        {
            var left = Left; var right = Right;
            for (int i = 0; i < Layers.Count; i++)
                if (ReferenceEquals(source, Layers[i].Left) || ReferenceEquals(source, Layers[i].Right))
                { left = Layers[i].Left; right = Layers[i].Right; break; }
            if (key == "left_ring_is_magic_item") return left?.Rarity == ItemRarity.Magic ? 1 : 0;
            if (key == "right_ring_is_magic_item") return right?.Rarity == ItemRarity.Magic ? 1 : 0;
            if (key is not ("other_ring_is_shaper_item" or "other_ring_is_elder_item")) return null;
            // The counterpart belongs to the physical source. Its own influence and other equipment cannot satisfy it.
            Item? other;
            if (ReferenceEquals(source, left)) other = right;
            else if (ReferenceEquals(source, right)) other = left;
            else return null;
            var influence = key == "other_ring_is_shaper_item" ? ItemInfluence.Shaper : ItemInfluence.Elder;
            return other is not null && (other.Influences & influence) != 0 ? 1 : 0;
        }
    }

    public static ConditionalRingSlots ConditionalRings(IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        IReadOnlyCollection<Item>? active = null)
    {
        Item? left = null, right = null;
        int leftCount = 0, rightCount = 0;
        for (int i = 0; i < equipment.Count; i++)
        {
            var e = equipment[i];
            if (e.Row is not (6 or 7)) continue;
            bool enabled = active is null || active.Contains(e.Item.Item, ReferenceEqualityComparer.Instance);
            if (e.Row == 6) { if (leftCount++ == 0 && enabled) left = e.Item.Item; }
            if (e.Row == 7) { if (rightCount++ == 0 && enabled) right = e.Item.Item; }
        }
        if (leftCount <= 1 && rightCount <= 1) return new(left, right);
        Item?[] At(byte row) => [.. equipment.Where(e => e.Row == row).Select(e =>
            active is null || active.Any(item => ReferenceEquals(item, e.Item.Item)) ? e.Item.Item : null)];
        var leftLayers = At(6); var rightLayers = At(7);
        return new(left, right)
        {
            Layers = [.. Enumerable.Range(0, Math.Max(leftLayers.Length, rightLayers.Length))
                .Select(i => (leftLayers.ElementAtOrDefault(i), rightLayers.ElementAtOrDefault(i)))],
        };
    }
}
