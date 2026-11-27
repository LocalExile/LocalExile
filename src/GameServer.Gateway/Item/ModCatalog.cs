using System.Collections.Immutable;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// Mods.dat (the Mods client table, read when the catalog is made): what the server needs to put mods on items
/// (Agents/docs/protocol/13-items.md §2a). The client identifies a mod by its HASH16 and counts its stats itself
/// (StatsKey1..8 up to the first empty one), so every <see cref="ItemMod"/> is built here with exactly that many values.
/// </summary>
public sealed class ModCatalog
{
    readonly string folder;
    readonly bool hardMode;
    readonly ModRangeProvider? ranges;
    readonly Lock _snapshotLock = new();

    public ModCatalog(string folder = "data", bool hardMode = false, ModRangeProvider? ranges = null)
    {
        this.folder = folder;
        this.hardMode = hardMode;
        this.ranges = ranges;
        Ranges = ranges ?? ModRangeProvider.Default;
        var (rows, byHash, byId) = Load(folder, hardMode, Ranges);
        var byRow = new Dictionary<int, Mod>(rows.Count);
        foreach (var m in rows) byRow[m.Row] = m;
        _stockSnapshot = new ModSnapshot(rows, byRow, byHash, byId, 0, 0);
        _snapshot = _stockSnapshot;
    }

    public ModRangeProvider Ranges { get; }

    sealed record RegisteredOverlay(long Id, string Owner, Mod Mod);
    sealed record ModSnapshot(List<Mod> Rows, Dictionary<int, Mod> ByRow, Dictionary<ushort, Mod> ByHash,
        Dictionary<string, Mod> ById, long LocalRevision, long SharedRevision);

    static ImmutableArray<RegisteredOverlay> _sharedOverlays = [];
    static long _sharedNextId;
    static long _sharedRevision;

    ImmutableArray<RegisteredOverlay> _localOverlays = [];
    long _localNextId;
    long _localRevision;
    readonly ModSnapshot _stockSnapshot;
    ModSnapshot _snapshot;

    /// <summary>Advances whenever shared runtime mod overlays change.</summary>
    public static long OverlayRevision => Volatile.Read(ref _sharedRevision);

    /// <summary>Advances whenever this catalog's instance or shared runtime mod overlays change.</summary>
    public long Revision => Volatile.Read(ref _localRevision) + Volatile.Read(ref _sharedRevision);

    /// <summary>Registers a runtime mod overlay visible to every <see cref="ModCatalog"/> instance, owned by <paramref name="owner"/>
    /// and automatically removed when that owner unloads.</summary>
    public static IDisposable RegisterOverlay(string owner, Mod mod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ValidateOverlay(mod);
        long id = Interlocked.Increment(ref _sharedNextId);
        ImmutableInterlocked.Update(ref _sharedOverlays, list => list.Add(new(id, owner, mod)));
        Interlocked.Increment(ref _sharedRevision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _sharedOverlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _sharedRevision);
        }));
    }

    /// <summary>Registers a runtime mod overlay on this <see cref="ModCatalog"/> instance, owned by <paramref name="owner"/>
    /// and automatically removed when that owner unloads.</summary>
    public IDisposable Register(string owner, Mod mod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ValidateOverlay(mod);
        long id = Interlocked.Increment(ref _localNextId);
        ImmutableInterlocked.Update(ref _localOverlays, list => list.Add(new(id, owner, mod)));
        Interlocked.Increment(ref _localRevision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _localOverlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _localRevision);
        }));
    }

    static void ValidateOverlay(Mod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (string.IsNullOrWhiteSpace(mod.Id))
            throw new ArgumentException("Mod.Id must be non-empty.", nameof(mod));
    }

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    /// <summary>A stat of a mod: its Stats.dat Id, the Stat{n}Min / Stat{n}Max roll range, and Stats.dat IsLocal (the
    /// stat changes the item it is on, not the character).</summary>
    public sealed record ModStat(uint Id, int Min, int Max, bool Local = false)
    {
        public bool Scalable { get; init; } = true;
        /// <summary>The stat's Stats.dat Id (for text and logs).</summary>
        public string Name => Program.Stats.Name(Id);
    }

    /// <summary>A Mods.dat row. GenerationType 1 prefix, 2 suffix, 3 unique / base implicit, 10 enchantment …;
    /// Domain 1 items.</summary>
    /// <summary>A Mods.dat row. Rolling (ItemRolls): SpawnWeights is the (Tags.dat Id, weight) list where the first tag the
    /// item has decides, GenerationWeights multiply that by weight / 100, Families exclude each other on one item.</summary>
    public sealed record Mod(int Row, string Id, ushort Hash, string Name, int Domain, int GenerationType, int Level,
        IReadOnlyList<ModStat> Stats)
    {
        public ModRangeProvider? Ranges { get; init; }
        public int MaxLevel { get; init; } = 100;
        public IReadOnlyList<int> Families { get; init; } = [];
        public IReadOnlyList<(string Tag, int Weight)> SpawnWeights { get; init; } = [];
        public IReadOnlyList<(string Tag, int Weight)> GenerationWeights { get; init; } = [];
        public bool EssenceOnly { get; init; }
        /// <summary>ModType.dat's Name for ModTypeKey: the Harvest conversion groups (ColdResistance, FireDamage …).</summary>
        public string ModType { get; init; } = "";
        public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
        public bool Unscalable { get; init; }
        /// <summary>What a mod does beyond its stats (monster mods: MonsterModCatalog): its BuffTemplates row (an aura or
        /// buff, BuffTemplate1), the monster it leaves on death (MonsterOnDeath) and its granted skills
        /// (GrantedEffectsPerLevelKeys).</summary>
        public int? BuffTemplate { get; init; }
        /// <summary>For a mod that is an aura (its BuffTemplates row lists who it reaches): whether the wearer is among
        /// them ("You and nearby Allies ...") or it is for others only ("Nearby Allies ...", "Nearby Enemies ...").
        /// Null for a mod without a template, or one whose template lists no one.</summary>
        public bool? AuraForSelf { get; init; }
        public string MonsterOnDeath { get; init; } = "";
        public int GrantedEffects { get; init; }
        public IReadOnlyList<int> GrantedEffectLevels { get; init; } = [];
        /// <summary>GrantedEffectsPerLevel overrides used by HardMode when this list is nonempty.</summary>
        public IReadOnlyList<int> RuthlessGrantedEffectLevels { get; init; } = [];

        /// <summary>The weight for an item with these tags: the first spawn tag it has, times the first generation tag
        /// it has / 100; 0 when no spawn tag matches.</summary>
        public int WeightFor(IReadOnlySet<string> tags)
        {
            int weight = SpawnWeights.FirstOrDefault(w => tags.Contains(w.Tag)).Weight;
            if (weight > 0 && GenerationWeights.FirstOrDefault(w => tags.Contains(w.Tag)) is { Tag: not null } g) weight = weight * g.Weight / 100;
            return weight;
        }

        /// <summary>The list a mod goes into when nothing else says so: prefixes and suffixes are explicit,
        /// enchantments enchants, everything else (base implicits, corruptions …) implicit.</summary>
        public ModSection DefaultSection => GenerationType switch { 1 or 2 => ModSection.Explicit, 10 or 21 or 22 => ModSection.Enchant, _ => ModSection.Implicit };

        /// <summary>The effective range of one stat: the database override when present, else the base Mods.dat range.</summary>
        public (int Min, int Max) RangeAt(int index) =>
            index >= 0 && index < Stats.Count
                ? Ranges?.Range(Id, index, Stats[index].Min, Stats[index].Max) ?? Normalise(Stats[index])
                : (0, 0);

        /// <summary>A range from world.db's mod_stat_overrides applies to this stat.</summary>
        public bool RangeOverridden(int index) => Ranges?.All.ContainsKey((Id, index)) == true;

        /// <summary>The effective maxima, in stat order (max-roll dev items and unique definitions use these).</summary>
        public IReadOnlyList<int> MaxValues() => [.. Stats.Select((_, i) => RangeAt(i).Max)];

        static (int Min, int Max) Normalise(ModStat stat) => (Math.Min(stat.Min, stat.Max), Math.Max(stat.Min, stat.Max));

        /// <summary>The mod with a value per stat rolled in its effective range, or in <paramref name="range"/> for a
        /// stat that gives one (a unique's own roll).</summary>
        public ItemMod Roll(ModSection section, Random random, Func<int, (int Min, int Max)?>? range = null)
        {
            var values = new int[Stats.Count];
            for (int i = 0; i < values.Length; i++)
            {
                var (min, max) = range?.Invoke(i) ?? RangeAt(i);
                values[i] = random.Next(Math.Min(min, max), Math.Max(min, max) + 1);
            }
            return new(section, Hash, values);
        }

        /// <summary>The mod with these values: missing ones are rolled, extra ones dropped (the client reads exactly one
        /// per stat).</summary>
        public ItemMod With(ModSection section, IReadOnlyList<int> values, Random random)
        {
            var rolled = Roll(section, random).Values;
            return new(section, Hash, [.. rolled.Select((v, i) => i < values.Count ? values[i] : v)]);
        }

        public override string ToString() => $"{Id} (row {Row})";
    }

    ModSnapshot CurrentSnapshot()
    {
        long localRev = Volatile.Read(ref _localRevision);
        long sharedRev = Volatile.Read(ref _sharedRevision);
        var current = Volatile.Read(ref _snapshot);
        if (current.LocalRevision == localRev && current.SharedRevision == sharedRev)
            return current;
        lock (_snapshotLock)
        {
            localRev = Volatile.Read(ref _localRevision);
            sharedRev = Volatile.Read(ref _sharedRevision);
            current = _snapshot;
            if (current.LocalRevision == localRev && current.SharedRevision == sharedRev)
                return current;
            var shared = _sharedOverlays;
            var local = _localOverlays;
            if (shared.IsEmpty && local.IsEmpty)
            {
                var stock = _stockSnapshot with { LocalRevision = localRev, SharedRevision = sharedRev };
                Volatile.Write(ref _snapshot, stock);
                return stock;
            }
            var rows = new List<Mod>(_stockSnapshot.Rows.Count + shared.Length + local.Length);
            rows.AddRange(_stockSnapshot.Rows);
            var byRow = new Dictionary<int, Mod>(_stockSnapshot.ByRow);
            var byHash = new Dictionary<ushort, Mod>(_stockSnapshot.ByHash);
            var byId = new Dictionary<string, Mod>(_stockSnapshot.ById, StringComparer.OrdinalIgnoreCase);
            var rowIndexInList = new Dictionary<int, int>(rows.Count);
            for (int i = 0; i < rows.Count; i++) rowIndexInList[rows[i].Row] = i;
            int nextSyntheticRow = _stockSnapshot.Rows.Count;

            void Apply(ImmutableArray<RegisteredOverlay> overlays)
            {
                for (int i = 0; i < overlays.Length; i++)
                {
                    var raw = overlays[i].Mod;
                    int assignedRow = raw.Row;
                    if (assignedRow < 0)
                    {
                        if (byId.TryGetValue(raw.Id, out var existing)) assignedRow = existing.Row;
                        else
                        {
                            while (byRow.ContainsKey(nextSyntheticRow)) nextSyntheticRow++;
                            assignedRow = nextSyntheticRow++;
                        }
                    }
                    var mod = raw with { Row = assignedRow, Ranges = raw.Ranges ?? Ranges };
                    if (byRow.TryGetValue(mod.Row, out var replacedByRow))
                    {
                        if (byHash.TryGetValue(replacedByRow.Hash, out var curHash) && ReferenceEquals(curHash, replacedByRow))
                            byHash.Remove(replacedByRow.Hash);
                        if (byId.TryGetValue(replacedByRow.Id, out var curId) && ReferenceEquals(curId, replacedByRow))
                            byId.Remove(replacedByRow.Id);
                    }
                    if (byId.TryGetValue(mod.Id, out var replacedById) && replacedById.Row != mod.Row)
                    {
                        if (byHash.TryGetValue(replacedById.Hash, out var curHash) && ReferenceEquals(curHash, replacedById))
                            byHash.Remove(replacedById.Hash);
                        byRow.Remove(replacedById.Row);
                        if (rowIndexInList.Remove(replacedById.Row, out int oldPos))
                        {
                            int lastIdx = rows.Count - 1;
                            if (oldPos < lastIdx)
                            {
                                var moved = rows[lastIdx];
                                rows[oldPos] = moved;
                                rowIndexInList[moved.Row] = oldPos;
                            }
                            rows.RemoveAt(lastIdx);
                        }
                    }
                    if (rowIndexInList.TryGetValue(mod.Row, out int pos))
                        rows[pos] = mod;
                    else
                    {
                        rowIndexInList[mod.Row] = rows.Count;
                        rows.Add(mod);
                    }
                    byRow[mod.Row] = mod;
                    byHash[mod.Hash] = mod;
                    byId[mod.Id] = mod;
                }
            }

            Apply(shared);
            Apply(local);
            var built = new ModSnapshot(rows, byRow, byHash, byId, localRev, sharedRev);
            Volatile.Write(ref _snapshot, built);
            return built;
        }
    }

    public bool Loaded => CurrentSnapshot().Rows.Count > 0;

    public Mod? Row(int row) => CurrentSnapshot().ByRow.GetValueOrDefault(row);

    public Mod? ByHash(ushort hash) => CurrentSnapshot().ByHash.GetValueOrDefault(hash);

    public Mod? ById(string id) => CurrentSnapshot().ById.GetValueOrDefault(id);

    public IReadOnlyList<Mod> All => CurrentSnapshot().Rows;

    /// <summary>A mod by row number, exact Id (any case), then the first Id that contains the text.</summary>
    public Mod? Find(string query)
    {
        if (int.TryParse(query, out int row)) return Row(row);
        var snap = CurrentSnapshot();
        return snap.ById.GetValueOrDefault(query)
            ?? snap.Rows.FirstOrDefault(m => m.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"SpellDamageOnWeaponImplicitWand1 implicit: spell_damage_+% 10" for logs and /item_mods.</summary>
    public string Describe(ItemMod m) => ByHash(m.Hash) is { } mod
        ? $"{mod.Id} {m.Section.ToString().ToLowerInvariant()}: " + string.Join(", ", mod.Stats.Select((s, i) => $"{s.Id} {(i < m.Values.Count ? m.Values[i] : 0)}"))
        : $"0x{m.Hash:x4} {m.Section.ToString().ToLowerInvariant()}: {string.Join(", ", m.Values)}";

    private static (List<Mod>, Dictionary<ushort, Mod>, Dictionary<string, Mod>) Load(string folder, bool hardMode,
        ModRangeProvider ranges)
    {
        var rows = new List<Mod>();
        if (!ClientTables.Has("Mods") || !ClientTables.Has("Stats")) return (rows, [], []);
        List<(string Id, bool Local, bool Scalable)> stats;
        var s = ClientTables.Table("Stats");
        stats = [.. s.Select(r => (r.Text("Id") ?? "", r.Bool("IsLocal"), r.BoolOrNull("IsScalable") ?? true))];
        List<string> modTypes = [];
        if (ClientTables.Has("ModType"))
            modTypes = [.. ClientTables.Table("ModType").Select(r => r.Text("Name") ?? "")];
        List<string> tags = [];
        if (ClientTables.Has("Tags"))
            tags = [.. ClientTables.Table("Tags").Select(r => r.Text("Id") ?? "")];
        // BuffTemplates' fourth column lists who an aura reaches: 1 its owner, 2 allies, 3 enemies, 10 minions.
        var templates = ClientTables.TableOrEmpty("BuffTemplates");
        bool? AuraReachesSelf(int? template)
        {
            if (template is not int row || templates.Row(row) is not { } aura) return null;
            int[] reaches;
            try { reaches = aura.Ints("__col4"); } catch { return null; }
            return reaches.Length == 0 ? null : Array.IndexOf(reaches, 1) >= 0;
        }
        List<(string, int)> Weights(ClientRow r, string keys, string values) =>
            [.. r.Ints(keys).Zip(r.Ints(values), (k, v) => (k < tags.Count ? tags[k] : $"#{k}", v))];
        foreach (var r in ClientTables.Table("Mods"))
        {
            bool alternate = hardMode && r.BoolOrNull("HasHardmodeStatValues") == true;
            string suffix = alternate ? "Hardmode" : "";
            // The client's stat iterator (0x1424371e0) stops at the first empty key, so the count does too.
            var modStats = new List<ModStat>();
            for (int i = 1; i <= 8; i++)
            {
                if (r.IntOrNull($"StatsKey{i}") is not int k) break;
                int key = k;
                var (_, local, scalable) = key >= 0 && key < stats.Count ? stats[key] : ("", false, true);
                uint statId = key >= 0 && key < stats.Count ? (uint)key + 1 : Stat.Of($"#{key}");
                // Explicit zero is meaningful (for example disabled boss rarity bonuses); it must not fall back.
                modStats.Add(new(statId, r.Int($"Stat{i}Min{suffix}"), r.Int($"Stat{i}Max{suffix}"), local)
                { Scalable = scalable });
            }
            rows.Add(new Mod(rows.Count, r.Text("Id") ?? "", (ushort)r.Int("HASH16"),
                r.Text("Name") ?? "", r.Int("Domain"), r.Int("GenerationType"),
                r.Int("Level"), modStats)
            {
                Ranges = ranges,
                MaxLevel = r.IntOrNull("MaxLevel") is int max && max > 0 ? max : 100,
                Families = [.. r.Ints("Families")],
                SpawnWeights = Weights(r, "SpawnWeight_TagsKeys", "SpawnWeight_Values"),
                GenerationWeights = Weights(r, "GenerationWeight_TagsKeys", "GenerationWeight_Values"),
                EssenceOnly = r.Bool("IsEssenceOnlyModifier"),
                Unscalable = r.BoolOrNull("IsUnscalable") == true,
                ModType = r.IntOrNull("ModTypeKey") is int mt && mt >= 0 && mt < modTypes.Count ? modTypes[mt] : "",
                BuffTemplate = r.IntOrNull("BuffTemplate1"),
                AuraForSelf = AuraReachesSelf(r.IntOrNull("BuffTemplate1")),
                MonsterOnDeath = r.TextOrNull("MonsterOnDeath") ?? "",
                GrantedEffects = r.Ints("GrantedEffectsPerLevelKeys").Length,
                GrantedEffectLevels = [.. r.Ints("GrantedEffectsPerLevelKeys")],
                RuthlessGrantedEffectLevels = [.. r.Ints("GrantedEffectRuthless")],
                Tags = new[] { "TagsKeys", "ImplicitTagsKeys" }
                    .SelectMany(key => r.Ints(key))
                    .Where(k => k >= 0 && k < tags.Count).Select(k => tags[k]).ToHashSet(),
            });
        }
        var byHash = new Dictionary<ushort, Mod>();
        var byId = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in rows) { byHash.TryAdd(m.Hash, m); byId.TryAdd(m.Id, m); }
        return (rows, byHash, byId);
    }
}
