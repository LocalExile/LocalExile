using System.Collections.Immutable;
using GameServer.Core.Protocol.Instance;

/// <summary>The pool a declarative <see cref="DropPoolEntry"/> targets.</summary>
public enum DropPoolCategory
{
    Currency,
    Equipment,
    Flask,
    Tincture,
    Gem,
    DivinationCard,
    Scarab,
    Unique,
    Boss,
    General,
}

/// <summary>Where a drop roll originated, used by <see cref="DropPoolEntry.Sources"/> to gate custom drop entries.</summary>
[Flags]
public enum DropSourceMask
{
    None = 0,
    MonsterKill = 1 << 0,
    Container = 1 << 1,
    Strongbox = 1 << 2,
    BossBonus = 1 << 3,
    MapCurrency = 1 << 4,
    All = MonsterKill | Container | Strongbox | BossBonus | MapCurrency,
}

/// <summary>Allowed monster rarities and boss archetypes for a <see cref="DropPoolEntry"/>.</summary>
[Flags]
public enum DropMonsterFilter
{
    None = 0,
    Normal = 1 << 0,
    Magic = 1 << 1,
    Rare = 1 << 2,
    Unique = 1 << 3,
    Boss = 1 << 4,
    Any = Normal | Magic | Rare | Unique | Boss,
}

/// <summary>
/// A declarative runtime drop-pool entry registered by a module or mod through <see cref="DropPoolOverlays"/>
/// (or <see cref="DropDataProvider.RegisterOverlay"/>). Owned by <see cref="GameEventOwners"/> so unloading the
/// registrant automatically removes the entry and advances <see cref="DropPoolOverlays.Revision"/>.
/// </summary>
/// <param name="Category">Which drop pool this entry participates in.</param>
/// <param name="ItemId">A <c>BaseItemTypes</c> Id or name, or (for <see cref="DropPoolCategory.Unique"/>,
/// <see cref="DropPoolCategory.Boss"/>, or <see cref="DropPoolCategory.General"/>) a unique definition name.</param>
/// <param name="Weight">Relative weight in the target pool (must be finite and positive when <see cref="Chance"/> is not set).</param>
public sealed record DropPoolEntry(DropPoolCategory Category, string ItemId, double Weight)
{
    /// <summary>Minimum area/monster level required for this entry to drop (inclusive; &lt;= 0 imposes no floor beyond the base type's DropLevel).</summary>
    public int MinAreaLevel { get; init; } = 1;

    /// <summary>Maximum area/monster level allowed for this entry to drop (inclusive; &lt;= 0 means no upper bound).</summary>
    public int MaxAreaLevel { get; init; }

    /// <summary>Allowed monster rarities and boss archetype (<see cref="DropMonsterFilter.Any"/> by default).</summary>
    public DropMonsterFilter Monsters { get; init; } = DropMonsterFilter.Any;

    /// <summary>Allowed drop sources (<see cref="DropSourceMask.All"/> by default).</summary>
    public DropSourceMask Sources { get; init; } = DropSourceMask.All;

    /// <summary>Optional monster variety IDs or substrings (case-insensitive; null or empty allows any monster).</summary>
    public IReadOnlyCollection<string>? MonsterArchetypes { get; init; }

    /// <summary>Optional league IDs (case-insensitive; null or empty allows every league).</summary>
    public IReadOnlyCollection<string>? Leagues { get; init; }

    /// <summary>Optional area IDs or <c>WorldAreas</c> tag IDs (case-insensitive; null or empty allows every area).</summary>
    public IReadOnlyCollection<string>? AreaTags { get; init; }

    /// <summary>Optional HardMode filter: null for both Standard and HardMode, true for HardMode only, false for Standard only.</summary>
    public bool? HardModeOnly { get; init; }

    /// <summary>Optional direct roll probability in (0, 1] for independent extra drops (<see cref="DropPoolCategory.Boss"/>,
    /// <see cref="DropPoolCategory.General"/>, or <see cref="DropPoolCategory.Unique"/>). When null, the entry competes by <see cref="Weight"/>.</summary>
    public double? Chance { get; init; }

    /// <summary>Stack size to emit for stackable items (clamped to the item's <c>MaxStack</c>; default 1).</summary>
    public ushort StackSize { get; init; } = 1;

    /// <summary>For <see cref="DropPoolCategory.Gem"/> entries, places the gem in the exceptional support sub-pool.</summary>
    public bool ExceptionalGem { get; init; }

    /// <summary>For <see cref="DropPoolCategory.Equipment"/> entries, restricts drops to Atlas map areas.</summary>
    public bool AtlasOnly { get; init; }

    /// <summary>For <see cref="DropPoolCategory.Flask"/> entries: true for the utility sub-pool, false for recovery, null to infer from class.</summary>
    public bool? IsUtilityFlask { get; init; }

    /// <summary>Returns whether this entry's gates match the given roll level and <paramref name="context"/>.</summary>
    public bool Matches(int level, in DropContext context, IReadOnlySet<string>? resolvedAreaTags = null)
    {
        if (MinAreaLevel > 0 && level > 0 && level < MinAreaLevel) return false;
        if (MaxAreaLevel > 0 && level > 0 && level > MaxAreaLevel) return false;
        if (Sources != DropSourceMask.All && (Sources & context.Source) == 0) return false;
        if (HardModeOnly is bool hm && hm != context.HardMode) return false;
        if (Category == DropPoolCategory.Boss && !context.IsBoss && context.Source != DropSourceMask.BossBonus)
            return false;
        if (Monsters != DropMonsterFilter.Any)
        {
            if (Monsters == DropMonsterFilter.None) return false;
            bool matched = false;
            if (context.IsBoss || context.Source == DropSourceMask.BossBonus)
                matched |= (Monsters & DropMonsterFilter.Boss) != 0;
            if (context.MonsterRarity is { } rarity)
            {
                var bit = rarity switch
                {
                    MonsterRarity.Normal => DropMonsterFilter.Normal,
                    MonsterRarity.Magic => DropMonsterFilter.Magic,
                    MonsterRarity.Rare => DropMonsterFilter.Rare,
                    MonsterRarity.Unique => DropMonsterFilter.Unique,
                    _ => DropMonsterFilter.None,
                };
                matched |= (Monsters & bit) != 0;
            }
            if (!matched) return false;
        }
        if (MonsterArchetypes is { Count: > 0 })
        {
            if (string.IsNullOrEmpty(context.MonsterVarietyId)) return false;
            bool archetypeHit = false;
            foreach (string pattern in MonsterArchetypes)
            {
                if (!string.IsNullOrEmpty(pattern)
                    && context.MonsterVarietyId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    archetypeHit = true;
                    break;
                }
            }
            if (!archetypeHit) return false;
        }
        if (Leagues is { Count: > 0 })
        {
            if (string.IsNullOrEmpty(context.LeagueId)) return false;
            bool leagueHit = false;
            foreach (string allowed in Leagues)
            {
                if (string.Equals(allowed, context.LeagueId, StringComparison.OrdinalIgnoreCase))
                {
                    leagueHit = true;
                    break;
                }
            }
            if (!leagueHit) return false;
        }
        if (AreaTags is { Count: > 0 })
        {
            bool tagHit = false;
            var tags = context.AreaTags ?? resolvedAreaTags;
            foreach (string wanted in AreaTags)
            {
                if (string.IsNullOrEmpty(wanted)) continue;
                if (!string.IsNullOrEmpty(context.AreaId)
                    && string.Equals(wanted, context.AreaId, StringComparison.OrdinalIgnoreCase))
                {
                    tagHit = true;
                    break;
                }
                if (tags is not null && (tags.Contains(wanted)
                    || tags.Any(t => string.Equals(t, wanted, StringComparison.OrdinalIgnoreCase))))
                {
                    tagHit = true;
                    break;
                }
            }
            if (!tagHit) return false;
        }
        return true;
    }
}

/// <summary>Contextual metadata for a drop roll so declarative <see cref="DropPoolEntry"/> filters can evaluate
/// source, monster rarity/boss status, variety archetype, area tags, league, and HardMode.</summary>
public readonly record struct DropContext(
    DropSourceMask Source = DropSourceMask.MonsterKill,
    MonsterRarity? MonsterRarity = null,
    bool IsBoss = false,
    string? MonsterVarietyId = null,
    string? AreaId = null,
    IReadOnlySet<string>? AreaTags = null,
    string? LeagueId = null,
    bool HardMode = false)
{
    public static DropContext MonsterKill(MonsterRarity rarity, bool isBoss = false, string? varietyId = null,
        string? areaId = null, IReadOnlySet<string>? areaTags = null, string? leagueId = null, bool hardMode = false) =>
        new(DropSourceMask.MonsterKill, rarity, isBoss, varietyId, areaId, areaTags, leagueId, hardMode);

    public static DropContext Container(string? areaId = null, IReadOnlySet<string>? areaTags = null,
        string? leagueId = null, bool hardMode = false) =>
        new(DropSourceMask.Container, null, false, null, areaId, areaTags, leagueId, hardMode);

    public static DropContext Strongbox(string? areaId = null, IReadOnlySet<string>? areaTags = null,
        string? leagueId = null, bool hardMode = false) =>
        new(DropSourceMask.Strongbox, null, false, null, areaId, areaTags, leagueId, hardMode);

    public static DropContext BossBonus(string? varietyId = null, string? areaId = null,
        IReadOnlySet<string>? areaTags = null, string? leagueId = null, bool hardMode = false) =>
        new(DropSourceMask.BossBonus, GameServer.Core.Protocol.Instance.MonsterRarity.Unique, true,
            varietyId, areaId, areaTags, leagueId, hardMode);

    public static DropContext MapCurrency(MonsterRarity rarity, bool isBoss = false, string? varietyId = null,
        string? areaId = null, IReadOnlySet<string>? areaTags = null, string? leagueId = null, bool hardMode = false) =>
        new(DropSourceMask.MapCurrency, rarity, isBoss, varietyId, areaId, areaTags, leagueId, hardMode);
}

/// <summary>
/// Thread-safe runtime registry of declarative <see cref="DropPoolEntry"/> registrations. Preserved across
/// <see cref="GameData.Reload"/> (<c>/reload</c>) and automatically cleaned up when the owning module or mod unloads
/// via <see cref="GameEventOwners"/>.
/// </summary>
public sealed class DropPoolOverlays
{
    public sealed record RegisteredEntry(long Id, string Owner, DropPoolEntry Entry);

    public static DropPoolOverlays Default { get; } = new();

    ImmutableArray<RegisteredEntry> _entries = [];
    long _nextId;
    long _revision;

    /// <summary>Advances on every registration or removal so <see cref="ItemDrops"/> lazily rebuilds its pools.</summary>
    public long Revision => Volatile.Read(ref _revision);

    /// <summary>Current immutable snapshot of active overlay entries.</summary>
    public ImmutableArray<RegisteredEntry> Snapshot => _entries;

    /// <summary>Registers a declarative drop-pool entry on <see cref="Default"/>, owned by <paramref name="owner"/>.</summary>
    public static IDisposable Register(string owner, DropPoolEntry entry) => Default.Add(owner, entry);

    /// <summary>Registers a batch of declarative drop-pool entries on <see cref="Default"/>, owned by <paramref name="owner"/>.</summary>
    public static IDisposable RegisterRange(string owner, IEnumerable<DropPoolEntry> entries) => Default.AddRange(owner, entries);

    /// <summary>Registers a declarative drop-pool entry on this overlay store, bound to <see cref="GameEventOwners"/>.</summary>
    public IDisposable Add(string owner, DropPoolEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        Validate(entry);
        long id = Interlocked.Increment(ref _nextId);
        var record = new RegisteredEntry(id, owner, entry);
        ImmutableInterlocked.Update(ref _entries, list => list.Add(record));
        Interlocked.Increment(ref _revision);
        var handle = new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _entries, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _revision);
        });
        return GameEventOwners.Own(owner, handle);
    }

    /// <summary>Registers a batch of declarative drop-pool entries on this overlay store, bound to <see cref="GameEventOwners"/>.</summary>
    public IDisposable AddRange(string owner, IEnumerable<DropPoolEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        if (list.Count == 0) return new Undo(() => { });
        var ids = new HashSet<long>(list.Count);
        var added = new List<RegisteredEntry>(list.Count);
        foreach (var entry in list)
        {
            Validate(entry);
            long id = Interlocked.Increment(ref _nextId);
            ids.Add(id);
            added.Add(new RegisteredEntry(id, owner, entry));
        }
        ImmutableInterlocked.Update(ref _entries, current => current.AddRange(added));
        Interlocked.Increment(ref _revision);
        var handle = new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _entries, current => current.RemoveAll(e => ids.Contains(e.Id))))
                Interlocked.Increment(ref _revision);
        });
        return GameEventOwners.Own(owner, handle);
    }

    static void Validate(DropPoolEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ItemId))
            throw new ArgumentException("DropPoolEntry.ItemId must be non-empty.", nameof(entry));
        if (!double.IsFinite(entry.Weight) || entry.Weight < 0)
            throw new ArgumentOutOfRangeException(nameof(entry), "DropPoolEntry.Weight must be finite and non-negative.");
        if (entry.MaxAreaLevel > 0 && entry.MinAreaLevel > entry.MaxAreaLevel)
            throw new ArgumentOutOfRangeException(nameof(entry), "DropPoolEntry.MinAreaLevel must not exceed MaxAreaLevel.");
        if (entry.Chance is double chance && (!double.IsFinite(chance) || chance < 0 || chance > 1))
            throw new ArgumentOutOfRangeException(nameof(entry), "DropPoolEntry.Chance must be in [0, 1].");
        bool hasChance = entry.Chance is double c && c > 0;
        if (hasChance && entry.Category is not (DropPoolCategory.Boss or DropPoolCategory.General or DropPoolCategory.Unique))
            throw new ArgumentOutOfRangeException(nameof(entry), "DropPoolEntry.Chance is only supported on Boss, General, and Unique categories.");
        if (!hasChance && entry.Weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(entry), "DropPoolEntry.Weight must be finite and positive when Chance is not set.");
    }

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}
