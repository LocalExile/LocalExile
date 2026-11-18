using GameServer.Core.Protocol.Instance;

/// <summary>
/// How many monsters guard a shrine and which (Agents/docs/protocol/14-monster-packs.md, "Shrine guards").
/// <para>The client data names guard packs only for the boss and influence shrines (<c>Shrines.MonsterPacks</c> →
/// <c>MonsterPacks</c>): the eight apparition shrines, 2–9 monsters a pack across their rows, in area-agnostic pack rows
/// (<c>WorldAreasKeys</c> [0], MinLevel 0, MaxLevel 100 — the shrine, not a world area, spawns them). The fifteen basic
/// shrine rows name none, and their rooms carry no monster points of their own
/// (<c>metadata/shrines/*.arm</c>: the statue and decals only), so a basic shrine is guarded by one of the area's own
/// packs at the pack's own MinCount–MaxCount: the wiki's "often surrounded by a pack of monsters guarding it", the
/// 1.0.0d note that shrine packs are not spawned too close to an entrance or waypoint, and the Atlas passives that make
/// a map shrine's guard pack magic (<c>map_shrines_have_guaranteed_magic_pack</c>) or add a chance of a second
/// (<c>map_shrines_additional_pack_chance_%</c>) all say the guard is an ordinary pack, one per shrine, normal by
/// default. Nothing in the data scales the count with area level, Act, map tier or difficulty; the count is the pack
/// row's own roll.</para>
/// <para>A shrine's rule is data plus these readings: the pack rows that guard it, the count when those rows give one,
/// the rarity, and whether the pack comes from the area's pool. Kept apart from the shrine's effects
/// (<c>ShrineRegistry</c>), so a shrine type's guards change with its row or this file, not with its buff.</para>
/// </summary>
public static class ShrineGuards
{
    /// <summary>A shrine type's guard rule: whether it has guards at all, whether the pack is one of the area's
    /// (<see cref="AreaPool"/>), the packs the data names (empty when <see cref="AreaPool"/>), the count range they give,
    /// a fixed count when one applies, and the rarity.</summary>
    public sealed record Rule(bool Required, bool AreaPool, IReadOnlyList<ShrineCatalog.GuardPack> Packs,
        int Min, int Max, int? Fixed, MonsterRarity Rarity);

    /// <summary>
    /// The rule of a shrine type in an area, with the packs of <see cref="Rule.Packs"/> that exist in MonsterPacks.
    /// <paramref name="mapArea"/> picks the endgame rule: a map may upgrade its shrine's guard pack through the Atlas
    /// passives, which are not implemented yet, so both branches are normal and the same count today; keeping the parameter
    /// is what lets a map's rule change without touching the campaign's.
    /// </summary>
    public static (Rule Rule, IReadOnlyList<PackCatalog.Pack> Packs) Resolve(ShrineCatalog.ShrineDefinition shrine,
        bool mapArea, Func<string, PackCatalog.Pack?> find)
    {
        if (shrine.GuardPacks.Count > 0)
        {
            var resolved = shrine.GuardPacks.Select(p => find(p.Id)).OfType<PackCatalog.Pack>().ToList();
            var rule = new Rule(true, false, shrine.GuardPacks,
                shrine.GuardPacks.Min(p => p.MinCount), shrine.GuardPacks.Max(p => p.MaxCount), null, MonsterRarity.Normal);
            return (rule, resolved);
        }
        // A basic shrine: one of the area's packs, at that pack's own count.
        return (new Rule(true, true, [], 0, 0, null, MonsterRarity.Normal), []);
    }

    /// <summary>Whether a shrine stands too close to the area's entrance (the arrival point) or an area transition —
    /// which is also where a waypoint's room and its exit are — to be guarded, within <paramref name="clearance"/> cells:
    /// the 1.0.0d patch note, "Monster packs around shrines are now not spawned if they are too close to an area entrance
    /// or waypoint".</summary>
    public static bool TooNearEntrance((int X, int Y) shrine, (int X, int Y) arrival, IReadOnlyList<(int X, int Y)> transitions,
        int clearance)
    {
        long Distance2((int X, int Y) to) => (long)(shrine.X - to.X) * (shrine.X - to.X) + (long)(shrine.Y - to.Y) * (shrine.Y - to.Y);
        long limit = (long)clearance * clearance;
        return Distance2(arrival) < limit || transitions.Any(t => Distance2(t) < limit);
    }
}
