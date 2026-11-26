using System.Collections.Immutable;
using System.Text.Json;
using GameServer.Core;

/// <summary>
/// Which gems can drop from monsters (19-gems.md §5). The data's SkillGems table also holds rows that never drop in 3.29:
/// Graft skills (the Chayula gems), item-granted and legacy skills (Envy, Death Aura, Quickstep, the single-level utility
/// rows) and placeholders such as "...". The drop pool is therefore the gems the game actually gives a player: the
/// QuestRewardOffers gems of every class, each class's starting gems (Characters StartSkillGem/StartSupportGem), the
/// drop-only exceptional supports (Empower, Enhance, Enlighten), and any runtime overlay gems registered via
/// <see cref="RegisterOverlay"/>.
/// </summary>
public static class GemSources
{
    sealed record RegisteredGem(long Id, string Owner, string BaseTypeId, bool Exceptional);

    static ImmutableArray<RegisteredGem> _overlays = [];
    static long _nextId;
    static long _revision;

    /// <summary>Advances whenever a runtime gem drop-pool overlay is added or removed.</summary>
    public static long Revision => Volatile.Read(ref _revision);

    public static readonly HashSet<string> ExceptionalSupports = new(StringComparer.Ordinal)
    {
        "Empower Support",
        "Enhance Support",
        "Enlighten Support",
    };

    static readonly string[] DropOnlyGems =
    [
        "Empower Support",
        "Enhance Support",
        "Enlighten Support",
        "Portal",
        "Detonate Mines",
    ];

    /// <summary>Registers a gem base type (by <c>BaseItemTypes</c> Id or name) into the natural gem drop pool until the
    /// handle is disposed or <paramref name="owner"/> unloads via <see cref="GameEventOwners"/>.</summary>
    public static IDisposable RegisterOverlay(string owner, string baseTypeId, bool exceptional = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseTypeId);
        long id = Interlocked.Increment(ref _nextId);
        ImmutableInterlocked.Update(ref _overlays, list => list.Add(new(id, owner, baseTypeId, exceptional)));
        Interlocked.Increment(ref _revision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _overlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _revision);
        }));
    }

    public static bool IsExceptionalSupport(ItemCatalog.BaseType type)
    {
        if (ExceptionalSupports.Contains(type.Name)) return true;
        var overlays = _overlays;
        for (int i = 0; i < overlays.Length; i++)
            if (overlays[i].Exceptional
                && (overlays[i].BaseTypeId.Equals(type.Id, StringComparison.OrdinalIgnoreCase)
                    || overlays[i].BaseTypeId.Equals(type.Name, StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    public static IReadOnlySet<int> DropPool(ItemCatalog items, string folder = "data")
    {
        var pool = new HashSet<int>();
        foreach (var offer in QuestRewardCatalog.Load(folder).Offers)
            foreach (var choice in offer.Choices)
                if (items.Row(choice.ItemRow) is { } type && items.Gem(type) is not null) pool.Add(type.Row);
        if (ClientTables.Has("Characters") && ClientTables.Has("SkillGems"))
        {
            var c = ClientTables.Table("Characters");
            var g = ClientTables.Table("SkillGems");
            foreach (var character in c)
                foreach (string column in new[] { "StartSkillGem", "StartSupportGem" })
                    if (character.IntOrNull(column) is int row && row >= 0 && row < g.Count
                        && g[row].IntOrNull("BaseItemTypesKey") is int baseRow
                        && items.Row(baseRow) is { } type && items.Gem(type) is not null)
                        pool.Add(type.Row);
        }
        foreach (string name in DropOnlyGems)
            if (items.Find(name) is { } type && items.Gem(type) is not null) pool.Add(type.Row);
        var overlays = _overlays;
        for (int i = 0; i < overlays.Length; i++)
            if (items.Find(overlays[i].BaseTypeId) is { Supported: true } type)
                pool.Add(type.Row);
        return pool;
    }

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}
