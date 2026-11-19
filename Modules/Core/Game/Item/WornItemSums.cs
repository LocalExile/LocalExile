using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // The conditions of a stat-sources lookup (ItemConditionFlags) ask the worn items what they carry on every lookup,
    // hundreds a second in a fight, and each summed the items' mods from scratch (live 2026-11-17: 96 of a lookup's
    // 108 us, under the combat lock). ItemModStats.Sum is a pure function of the items (immutable records), the mod
    // and stat catalogs, the game rules and, for the recent-event form, the events: the sums below are remembered for
    // exactly those inputs. The tables are this module's and empty with it.
    sealed record ItemSum(ModCatalog Mods, object? Rules, IReadOnlyDictionary<string, int> Sum);
    static readonly RuleTable<Item, ItemSum> ItemSums = new();

    /// <summary>One item's global mod stats with no subject: <c>ItemModStats.Sum([item], mods)</c>.</summary>
    internal static IReadOnlyDictionary<string, int> PlainItemSum(Item item, ModCatalog mods)
    {
        var rules = Program.GameLogicOrNull;
        if (ItemSums.TryGetValue(item, out var known) && ReferenceEquals(known.Mods, mods) && ReferenceEquals(known.Rules, rules))
            return known.Sum;
        var sum = ItemModStats.Sum([item], mods);
        ItemSums.AddOrUpdate(item, new(mods, rules, sum));
        return sum;
    }

    sealed record WornSum(Item[] Items, ModCatalog Mods, object? Rules, object Stats, HashSet<string>? Recent,
        IReadOnlyDictionary<string, int> Sum);
    // The callers pass a few different views of the worn items (all, the active ones): one remembered sum for each.
    sealed class WornSums { public readonly WornSum?[] Kept = new WornSum?[4]; public int Next; }
    static readonly RuleTable<ClientState, WornSums> WornSumsOf = new();

    /// <summary>The items' global mod stats together: with no subject, or, given the recent events, with the stats
    /// that depend on them decided and folded onto their plain names (as SelfCurseConditionInputs sums its gear).</summary>
    static IReadOnlyDictionary<string, int> WornItemSum(ClientState conn, IEnumerable<Item> items, IReadOnlySet<string>? recent = null)
    {
        var list = items as Item[] ?? [.. items];
        var mods = ItemsFor(conn).Mods;
        var rules = Program.GameLogicOrNull;
        var memo = WornSumsOf.GetOrCreateValue(conn);
        foreach (var kept in memo.Kept)
            if (kept is not null && ReferenceEquals(kept.Mods, mods) && ReferenceEquals(kept.Rules, rules)
                && ReferenceEquals(kept.Stats, Stats) && SameItems(kept.Items, list)
                && (recent is null ? kept.Recent is null : kept.Recent is not null && kept.Recent.SetEquals(recent)))
                return kept.Sum;
        var sum = recent is null ? ItemModStats.Sum(list, mods)
            : ItemModStats.Sum(list, mods, subject: new(Recent: recent, KnownRecent: TrackedItemRecentEvents), statExists: Stats.Has);
        // The entry owns its inputs: a caller's array or set may change after this call.
        memo.Kept[(uint)Interlocked.Increment(ref memo.Next) % memo.Kept.Length] =
            new([.. list], mods, rules, Stats, recent is null ? null : [.. recent], sum);
        return sum;
    }

    static bool SameItems(Item[] a, Item[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    // WornGrantedSkills.For reads every mod of every worn item; its answer is a pure function of the catalog and of
    // the items in their rows. The Physical Aegis condition asks on every stat-sources lookup (12 us of one).
    sealed record WornGrants(WornGrantedSkills Owner, (byte Row, InventoryItem Item)[] Equipment, IReadOnlyList<WornGrantedSkills.Grant> Grants);
    static readonly RuleTable<ClientState, WornGrants> WornGrantsOf = new();

    static IReadOnlyList<WornGrantedSkills.Grant> WornGrantsFor(ClientState conn, IReadOnlyList<(byte Row, InventoryItem Item)> equipment)
    {
        var owner = WornGrantedSkillOwner(conn);
        if (WornGrantsOf.TryGetValue(conn, out var known) && ReferenceEquals(known.Owner, owner) && Same(known.Equipment, equipment))
            return known.Grants;
        WornGrantedSkills.Grant[] grants = [.. owner.For(equipment)];
        WornGrantsOf.AddOrUpdate(conn, new(owner, [.. equipment], grants));
        return grants;

        // An inventory item is a new record whenever its place, its id or its item changes.
        static bool Same((byte Row, InventoryItem Item)[] a, IReadOnlyList<(byte Row, InventoryItem Item)> b)
        {
            if (a.Length != b.Count) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].Row != b[i].Row || !ReferenceEquals(a[i].Item, b[i].Item)) return false;
            return true;
        }
    }
}
