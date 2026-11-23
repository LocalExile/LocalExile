using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Tinctures (Tinctures.dat and BuffDefinitions): equipped in flask belt slots, toggled on/off with C→S 0x70, apply
/// weapon modifiers while active, accumulate Mana Burn (or Weeping Wounds under Bloodsoaked Blade) over time,
/// automatically deactivate at 0 Mana (or 1 Life / mastery stack cap), and trigger a shared cooldown on deactivation.
/// </summary>
public static class Tinctures
{
    public const string ManaBurnBuffId = "tincture_mana_burn";
    public const string LifeBurnBuffId = "tincture_life_burn";
    public const string ParentBuffId = "tincture_parent_buff";
    public const int HighToxicityThreshold = 12;

    /// <summary>One base tincture's native timing and buff identity from Tinctures.dat and BuffDefinitions.dat.</summary>
    public sealed record Definition(int BaseRow, double DebuffIntervalSeconds, double CooldownSeconds, ushort BuffRow, string BuffId);

    /// <summary>Shared tincture BuffDefinitions rows resolved from client.db.</summary>
    public sealed record BuffRows(ushort ManaBurn, ushort LifeBurn, ushort Parent, IReadOnlyDictionary<string, ushort> Uniques);

    /// <summary>Catalog load result: all usable tincture definitions, the native 3.25+ base rows, and shared buff rows.</summary>
    public sealed record Catalog(Dictionary<int, Definition> ByRow, HashSet<int> NativeRows, BuffRows Buffs);

    static readonly Dictionary<string, string> BaseBuffIds = new(StringComparer.Ordinal)
    {
        ["Metadata/Items/Tinctures/Tincture1"] = "tincture_prismatic",
        ["Metadata/Items/Tinctures/Tincture2"] = "tincture_rosethorn",
        ["Metadata/Items/Tinctures/Tincture3"] = "tincture_ironwood",
        ["Metadata/Items/Tinctures/Tincture4"] = "tincture_ashbark",
        ["Metadata/Items/Tinctures/Tincture5"] = "tincture_borealwood",
        ["Metadata/Items/Tinctures/Tincture6"] = "tincture_fulgurite",
        ["Metadata/Items/Tinctures/Tincture7"] = "tincture_poisonberry",
        ["Metadata/Items/Tinctures/Tincture8"] = "tincture_bloodsap",
        ["Metadata/Items/Tinctures/Tincture9"] = "tincture_oakbranch",
        ["Metadata/Items/Tinctures/Tincture10"] = "tincture_sporebloom",
    };

    static readonly (string ModId, string BuffId)[] UniqueBuffMods =
    [
        ("TincturePenetrationPerToxicityUnique__1", "tincture_unique1"),
        ("TinctureMeleeSplashOnWeaponHitUnique__1", "tincture_unique2"),
        ("TinctureFireDamageTakenPerToxicityUnique__1", "tincture_unique3"),
        ("TinctureToxicityOnHitUnique__1", "tincture_unique4"),
        ("TinctureApplyWitherStacksOnHitUnique__1", "tincture_unique5"),
    ];

    static readonly HashSet<uint> UnscaledStats =
    [
        Stat.CullingStrikeWithTincturedWeapons,
        Stat.MeleeSplashWithTincturedWeapons,
        Stat.ToxicityStacksGainedOnHitWithTincturedWeapons,
        Stat.AllDamageWithTincturedWeaponsCanIgnite,
        Stat.AllDamageWithTincturedWeaponsCanShock,
        Stat.AllDamageWithTincturedWeaponsCanFreeze,
        Stat.AllDamageWithTincturedWeaponsCanPoison,
        Stat.AlwaysStunEnemiesThatAreOnFullLifeWithTincturedWeapons,
        Stat.TincturedWeaponHitsCannotBeEvadedWhileOnLowLife,
    ];

    public static Catalog Load(string folder, IReadOnlyList<ItemCatalog.BaseType> rows)
    {
        var byRow = new Dictionary<int, Definition>();
        var nativeRows = new HashSet<int>();
        var buffById = new Dictionary<string, ushort>(StringComparer.Ordinal);
        if (ClientTables.Has("BuffDefinitions"))
        {
            int idx = 0;
            foreach (var r in ClientTables.Table("BuffDefinitions"))
            {
                if (r.TextOrNull("Id") is { Length: > 0 } id && idx <= ushort.MaxValue)
                    buffById.TryAdd(id, (ushort)idx);
                idx++;
            }
        }
        var uniques = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (var (_, buffId) in UniqueBuffMods)
            if (buffById.TryGetValue(buffId, out ushort row)) uniques[buffId] = row;
        var buffs = new BuffRows(
            buffById.GetValueOrDefault(ManaBurnBuffId),
            buffById.GetValueOrDefault(LifeBurnBuffId),
            buffById.GetValueOrDefault(ParentBuffId),
            uniques);

        if (!ClientTables.Has("Tinctures")) return new(byRow, nativeRows, buffs);
        var byName = new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ClientTables.Table("Tinctures"))
        {
            if (r.IntOrNull("BaseItem") is not int b || b < 0 || b >= rows.Count) continue;
            var baseType = rows[b];
            string buffId = BaseBuffIds.GetValueOrDefault(baseType.Id, "");
            ushort buffRow = buffId.Length > 0 ? buffById.GetValueOrDefault(buffId) : (ushort)0;
            double interval = Math.Max(0.05, r.Int("DebuffInterval") / 1000.0);
            double cooldown = Math.Max(0.1, r.Int("Cooldown") / 1000.0);
            var def = new Definition(b, interval, cooldown, buffRow, buffId);
            if (byRow.TryAdd(b, def))
            {
                nativeRows.Add(b);
                byName.TryAdd(baseType.Name, def);
            }
        }
        // Legacy 3.23 tincture bases (rows without a Tinctures.dat entry) inherit the matching named 3.25+ base's timing.
        foreach (var baseType in rows)
            if (baseType.Class == "Tincture" && !byRow.ContainsKey(baseType.Row)
                && byName.TryGetValue(baseType.Name, out var fallback))
                byRow[baseType.Row] = fallback with { BaseRow = baseType.Row };
        return new(byRow, nativeRows, buffs);
    }

    public static ushort ResolveBuffRow(Item item, ModCatalog mods, Definition def, BuffRows buffs)
    {
        if (item.Rarity == ItemRarity.Unique)
            foreach (var mod in item.Mods)
                if (mods.ByHash(mod.Hash) is { } row)
                    foreach (var (modId, buffId) in UniqueBuffMods)
                        if (row.Id == modId && buffs.Uniques.TryGetValue(buffId, out ushort buffRow) && buffRow != 0)
                            return buffRow;
        return def.BuffRow;
    }

    /// <summary>Tincture effect multiplier: 1 + (quality + local effect + character effect) / 100.</summary>
    public static double EffectMultiplier(Item item, IReadOnlyDictionary<uint, int> local, Func<uint, int> character,
        int emptyFlaskSlots = 0, bool highToxicity = false, bool usedLifeFlaskRecently = false, bool usingFlask = false)
    {
        long percent = 100L + item.Quality
            + local.GetValueOrDefault(Stat.LocalTinctureModEffectPlusPct)
            + character(Stat.BaseTinctureModEffectPlusPct)
            + character(Stat.TinctureModEffectPlusPct)
            + (long)Math.Max(0, emptyFlaskSlots) * character(Stat.TinctureModEffectPlusPctPerEmptyFlaskSlot)
            + (highToxicity ? character(Stat.TinctureEffectPlusPctAtHighToxicity) : 0)
            + (usedLifeFlaskRecently ? character(Stat.TinctureEffectPlusPctIfUsedLifeFlaskRecently) : 0)
            + (!usingFlask ? character(Stat.TinctureEffectPlusPctWhileNotUsingFlask) : 0);
        return Math.Max(0, percent) / 100.0;
    }

    /// <summary>All non-local stats of a tincture item, scaled by its tincture effect multiplier (flag stats stay unscaled).</summary>
    public static IReadOnlyList<(uint Stat, int Value)> ScaledMods(Item item, ModCatalog mods, double effect)
    {
        var totals = new Dictionary<uint, long>();
        foreach (var (id, value) in ItemModStats.Values(item, mods, local: false))
            totals[id] = totals.GetValueOrDefault(id) + value;
        if (totals.Count == 0) return [];
        var list = new List<(uint Stat, int Value)>(totals.Count);
        foreach (var (id, raw) in totals)
        {
            if (raw == 0) continue;
            int clamped = (int)Math.Clamp(raw, int.MinValue, int.MaxValue);
            int scaled = UnscaledStats.Contains(id) ? clamped : UtilityFlasks.Scale(clamped, effect);
            if (scaled != 0) list.Add((id, scaled));
        }
        return list;
    }

    /// <summary>Interval in seconds between Mana Burn / Weeping Wounds stacks; +infinity when periodic burn is disabled.</summary>
    public static double DebuffInterval(Definition def, IReadOnlyDictionary<uint, int> local, Func<uint, int> character)
    {
        if (local.GetValueOrDefault(Stat.LocalCannotGenerateToxicityStacksOverTime) != 0)
            return double.PositiveInfinity;
        double increased = Math.Max(0.1, (100.0 + local.GetValueOrDefault(Stat.LocalTinctureToxicityRatePlusPct)
            + character(Stat.TinctureToxicityRatePlusPct) + character(Stat.VirtualTinctureToxicityRatePlusPct)) / 100.0);
        double more = Math.Max(0.0, (100.0 + character(Stat.WardenTinctureToxicityRatePlusPctFinal)) / 100.0)
            * Math.Max(0.0, (100.0 + character(Stat.VirtualTinctureToxicityRatePlusPctFinal)) / 100.0);
        double rate = Math.Max(0.1, increased * more);
        return Math.Max(0.05, def.DebuffIntervalSeconds / rate);
    }

    /// <summary>Cooldown duration in seconds triggered when this tincture deactivates.</summary>
    public static double CooldownSeconds(Definition def, IReadOnlyDictionary<uint, int> local, Func<uint, int> character)
    {
        double rate = Math.Max(0.1, (100.0 + local.GetValueOrDefault(Stat.LocalTinctureCooldownRecoveryPlusPct)
            + character(Stat.TinctureCooldownRecoveryPlusPct)) / 100.0);
        return Math.Max(0.1, def.CooldownSeconds / rate);
    }

    /// <summary>Mana (or Life under Bloodsoaked Blade) lost per second from Mana Burn / Weeping Wounds stacks:
    /// 1% of maximum resource per effective stack per second (minimum 1 per effective stack per second).</summary>
    public static double BurnPerSecond(int stacks, uint maxResource, int ignoredStacks = 0)
    {
        int effective = Math.Max(0, stacks - Math.Max(0, ignoredStacks));
        if (effective <= 0) return 0;
        return effective * Math.Max(1.0, maxResource * 0.01);
    }

    public sealed record Active(uint ItemId, byte Slot, string Name, Item Item, Definition Definition,
        ushort Buff, ushort BuffRow, double ActivatedAt, double DebuffInterval, double CooldownDuration,
        IReadOnlyDictionary<uint, int> Local, double InitialEffectMultiplier,
        IReadOnlyList<(uint Stat, int Value)> InitialScaledStats)
    {
        public double NextBurnAt { get; set; } = double.IsInfinity(DebuffInterval)
            ? double.PositiveInfinity : ActivatedAt + DebuffInterval;
        public double EffectMultiplier { get; set; } = InitialEffectMultiplier;
        public IReadOnlyList<(uint Stat, int Value)> ScaledStats { get; set; } = InitialScaledStats;
    }

    public sealed record Lingering(uint ItemId, byte Slot, Item Item, string Name, ushort Buff, ushort BuffRow,
        double Ends, IReadOnlyList<(uint Stat, int Value)> ScaledStats);

    /// <summary>Reload-persistent per-player tincture state held on <see cref="Player"/>.</summary>
    public sealed class Effects
    {
        readonly List<Active> _active = [];
        readonly List<Lingering> _lingering = [];
        readonly List<uint> _pendingBarInactive = [];
        readonly List<(byte Slot, int Charges)> _pendingAdjacentCharges = [];
        readonly Lock _lock = new();
        volatile bool _anyActive;
        volatile bool _anyEffective;
        volatile bool _hasLingering;
        volatile bool _hasPendingBarInactive;
        volatile bool _hasPendingAdjacentCharges;
        long _revision;

        public double CooldownUntil { get; private set; } = double.NegativeInfinity;
        public double LastAdjacentChargeGrantAt { get; set; } = double.NegativeInfinity;
        public int BurnStacks { get; private set; }
        public bool LifeBurn { get; set; }
        public ushort BurnBuff { get; set; }
        public int PresentedBurnStacks { get; set; }
        public bool PresentedLifeBurn { get; set; }
        public ushort ParentBuff { get; set; }
        public double LifeBurnCarry;

        public bool AnyActive => _anyActive;
        public bool AnyEffective => _anyEffective;
        public bool HasLingering => _hasLingering;
        public bool HasPendingBarInactive => _hasPendingBarInactive;
        public bool HasPendingAdjacentCharges => _hasPendingAdjacentCharges;
        public long Revision => Volatile.Read(ref _revision);

        public bool HasActiveAdjacentTo(byte slot)
        {
            if (!_anyActive) return false;
            lock (_lock) return _active.Any(a => Math.Abs(a.Slot - slot) == 1);
        }

        public void QueuePendingBarInactive(uint itemId)
        {
            lock (_lock)
            {
                if (!_pendingBarInactive.Contains(itemId)) _pendingBarInactive.Add(itemId);
                _hasPendingBarInactive = true;
            }
        }

        public List<uint>? DrainPendingBarInactive()
        {
            if (!_hasPendingBarInactive) return null;
            lock (_lock)
            {
                if (_pendingBarInactive.Count == 0) { _hasPendingBarInactive = false; return null; }
                var ids = new List<uint>(_pendingBarInactive);
                _pendingBarInactive.Clear();
                _hasPendingBarInactive = false;
                return ids;
            }
        }

        public void QueuePendingAdjacentCharges(byte slot, int charges)
        {
            if (charges <= 0) return;
            lock (_lock)
            {
                _pendingAdjacentCharges.Add((slot, charges));
                _hasPendingAdjacentCharges = true;
            }
        }

        public List<(byte Slot, int Charges)>? DrainPendingAdjacentCharges()
        {
            if (!_hasPendingAdjacentCharges) return null;
            lock (_lock)
            {
                if (_pendingAdjacentCharges.Count == 0) { _hasPendingAdjacentCharges = false; return null; }
                var list = new List<(byte Slot, int Charges)>(_pendingAdjacentCharges);
                _pendingAdjacentCharges.Clear();
                _hasPendingAdjacentCharges = false;
                return list;
            }
        }

        public bool UpdateScaling(uint itemId, double multiplier, IReadOnlyList<(uint Stat, int Value)> scaled)
        {
            lock (_lock)
            {
                var entry = _active.FirstOrDefault(a => a.ItemId == itemId);
                if (entry is null) return false;
                entry.EffectMultiplier = multiplier;
                entry.ScaledStats = scaled;
                Interlocked.Increment(ref _revision);
                return true;
            }
        }

        public int ActiveCount
        {
            get { lock (_lock) return _active.Count; }
        }

        public bool OnCooldown(double now)
        {
            lock (_lock) return now < CooldownUntil;
        }

        public double CooldownRemaining(double now)
        {
            lock (_lock) return Math.Max(0, CooldownUntil - now);
        }

        public Active? Of(uint itemId)
        {
            lock (_lock) return _active.FirstOrDefault(a => a.ItemId == itemId);
        }

        public bool IsActive(uint itemId)
        {
            if (!_anyActive) return false;
            lock (_lock) return _active.Any(a => a.ItemId == itemId);
        }

        public IReadOnlyList<Active> ActiveSnapshot()
        {
            if (!_anyActive) return [];
            lock (_lock) return [.. _active];
        }

        public void Activate(Active effect)
        {
            lock (_lock)
            {
                _active.RemoveAll(a => a.ItemId == effect.ItemId);
                _active.Add(effect);
                SyncFlagsLocked();
            }
        }

        public Active? Deactivate(uint itemId, double now, out bool allInactive, double lingerSeconds = 0, ushort lingerBuff = 0)
        {
            lock (_lock)
            {
                int idx = _active.FindIndex(a => a.ItemId == itemId);
                if (idx < 0) { allInactive = _active.Count == 0; return null; }
                var removed = _active[idx];
                _active.RemoveAt(idx);
                CooldownUntil = Math.Max(CooldownUntil, now + removed.CooldownDuration);
                if (lingerSeconds > 0 && removed.ScaledStats.Count > 0)
                {
                    _lingering.RemoveAll(l => l.ItemId == removed.ItemId);
                    _lingering.Add(new(removed.ItemId, removed.Slot, removed.Item, removed.Name, lingerBuff, removed.BuffRow,
                        now + lingerSeconds, removed.ScaledStats));
                }
                allInactive = _active.Count == 0;
                if (allInactive)
                {
                    BurnStacks = 0;
                    LifeBurnCarry = 0;
                }
                SyncFlagsLocked();
                return removed;
            }
        }

        public (List<Active> Removed, List<Lingering> Lingering) DeactivateAll(double now, double lingerSeconds = 0,
            Func<Active, ushort>? lingerBuffFor = null)
        {
            lock (_lock)
            {
                if (_active.Count == 0) return ([], []);
                var removed = new List<Active>(_active);
                List<Lingering> lingering = [];
                _active.Clear();
                foreach (var a in removed)
                {
                    CooldownUntil = Math.Max(CooldownUntil, now + a.CooldownDuration);
                    if (lingerSeconds > 0 && a.ScaledStats.Count > 0)
                    {
                        _lingering.RemoveAll(l => l.ItemId == a.ItemId);
                        var entry = new Lingering(a.ItemId, a.Slot, a.Item, a.Name, lingerBuffFor?.Invoke(a) ?? 0, a.BuffRow,
                            now + lingerSeconds, a.ScaledStats);
                        _lingering.Add(entry);
                        lingering.Add(entry);
                    }
                }
                BurnStacks = 0;
                LifeBurnCarry = 0;
                SyncFlagsLocked();
                return (removed, lingering);
            }
        }

        public List<Lingering> EndLingering(Func<Lingering, bool> endedWhen)
        {
            if (!_hasLingering) return [];
            lock (_lock)
            {
                var ended = _lingering.Where(endedWhen).ToList();
                if (ended.Count == 0) return ended;
                foreach (var l in ended) _lingering.Remove(l);
                SyncFlagsLocked();
                return ended;
            }
        }

        public int AddBurnStacks(int delta)
        {
            if (delta <= 0) return BurnStacks;
            lock (_lock)
            {
                if (_active.Count == 0) return 0;
                BurnStacks = (int)Math.Min(ushort.MaxValue, (long)BurnStacks + delta);
                Interlocked.Increment(ref _revision);
                return BurnStacks;
            }
        }

        public int RemoveBurnStacks(int delta)
        {
            if (delta <= 0 || BurnStacks == 0) return BurnStacks;
            lock (_lock)
            {
                int next = Math.Max(0, BurnStacks - delta);
                if (next == BurnStacks) return BurnStacks;
                BurnStacks = next;
                Interlocked.Increment(ref _revision);
                return BurnStacks;
            }
        }

        public (IReadOnlyList<(uint Stat, int Value)> Stats, int Stacks, bool Active, bool Lingering) EffectiveSnapshot()
        {
            if (!_anyEffective) return ([], 0, false, false);
            lock (_lock)
            {
                if (_active.Count == 0 && _lingering.Count == 0) return ([], 0, false, false);
                var stats = new List<(uint Stat, int Value)>();
                foreach (var a in _active) stats.AddRange(a.ScaledStats);
                foreach (var l in _lingering) stats.AddRange(l.ScaledStats);
                return (stats, BurnStacks, _active.Count > 0, _lingering.Count > 0);
            }
        }

        void SyncFlagsLocked()
        {
            _anyActive = _active.Count > 0;
            _hasLingering = _lingering.Count > 0;
            _anyEffective = _anyActive || _hasLingering;
            Interlocked.Increment(ref _revision);
        }
    }
}
