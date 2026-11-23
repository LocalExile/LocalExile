using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // Kalandra's Touch ("Reflects opposite Ring", local_ring_duplicate_other_ring): a ring with the stat in one ring slot
    // gives the modifiers of the active ring in the other slot as well (Agents/docs/protocol/13-items.md §2e).
    public const uint ReflectRingStat = Stat.LocalRingDuplicateOtherRing;
    public const byte LeftRing = 6, RightRing = 7;

    public static bool ReflectsOtherRing(Item item, ModCatalog mods) =>
        item.Mods.Any(m => mods.ByHash(m.Hash)?.Stats.Any(s => s.Id == ReflectRingStat) == true);

    /// <summary>The reflections the worn rings make: for a reflecting ring in the left or right ring slot, a copy of the
    /// ring in the other slot without its sockets, when both are <paramref name="active"/> and that ring does not reflect
    /// too. Added to the active items, the copy's modifiers count a second time wherever the equipment is summed.</summary>
    public static IEnumerable<Item> RingReflections(IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        Func<InventoryItem, bool> active, ModCatalog mods)
    {
        InventoryItem? At(byte row) => equipment.FirstOrDefault(e => e.Row == row && active(e.Item)).Item;
        if (At(LeftRing) is not { } left || At(RightRing) is not { } right) yield break;
        bool leftReflects = ReflectsOtherRing(left.Item, mods), rightReflects = ReflectsOtherRing(right.Item, mods);
        if (leftReflects == rightReflects) yield break;
        // One copy per ring, so the item caches keyed by reference (ItemModStats.Local) keep working.
        yield return Reflections.GetValue(leftReflects ? right.Item : left.Item, ring => ring with { Sockets = [], Links = null });
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Item, Item> Reflections = new();
}
