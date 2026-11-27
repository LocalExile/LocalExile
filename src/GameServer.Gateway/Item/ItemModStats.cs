using GameServer.Core.Protocol.Instance;

/// <summary>
/// What equipped items' mods do (Agents/docs/protocol/13-items.md §2c). Every mod list counts (implicit, explicit,
/// enchant, scourge, hidden), identified or not, as in the game. A stat that Stats.dat marks IsLocal changes the item it
/// is on (<see cref="Local"/>: a weapon's critical chance, attack speed, accuracy …); every other stat is the
/// character's and is added to the passives' stats (<see cref="Global"/>), so it reaches the pools and the stats the
/// client shows the same way.
/// </summary>
public static class ItemModStats
{
    /// <summary>A rule's replacement for one modifier value, or null to use the existing effect calculation.</summary>
    public delegate int? ModifierValueRule(ItemMod mod, ModCatalog.Mod row, int statIndex, long ordinaryEffect);

    /// <summary>The equipment rows whose items count: body armour, the first weapon set, helmet, amulet, rings, gloves,
    /// boots and belt (Inventories.dat rows 1-10), and the third ring slot (row 102, Ring3, shown by The Unseen Hand's
    /// <c>enable_ring_slot_3</c>). The second weapon set (14, 15) and the flasks (11) do not.</summary>
    public static readonly byte[] EquipmentRows = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 102];

    /// <summary>The item-local stats that raise the effect of the item's own explicit modifiers: every explicit one,
    /// or only that side (Simplex and Focused Amulets, the Simplex ring bases, Subsume the Source).</summary>
    const uint ExplicitEffect = Stat.LocalExplicitModEffectPlusPct, PrefixEffect = Stat.LocalPrefixModEffectPlusPct,
        SuffixEffect = Stat.LocalSuffixModEffectPlusPct;

    /// <summary>The summed values of the stats on these items' mods, by Stats.dat Id: global ones, or with
    /// <paramref name="local"/> the item's own. An explicit modifier counts with the item's increased effect applied
    /// (<see cref="Effect"/>).
    /// <para>
    /// <paramref name="subject"/> is the character the stats are for, so a stat whose id carries a condition
    /// (<c>attack_speed_while_dual_wielding_+%</c>) only counts when that condition holds (issue #504). Without one
    /// every condition is unmodelled and every stat counts, exactly as before.
    /// </para>
    /// <para>
    /// <paramref name="statExists"/> says whether a stat id is one this build has. When it is given, a conditional stat
    /// that holds and has no consumer of its own is summed onto its plain name (<c>spell_damage_+%</c>), which the
    /// pools, panels and damage pipeline already read — otherwise the condition would be decided but nothing would read
    /// the value. A caller that does not give it (or gives none) keeps every id as it is.
    /// </para></summary>
    public static Dictionary<uint, int> Sum(IEnumerable<Item> items, ModCatalog mods, bool local = false,
        StatConditions.Subject subject = default, Func<string, bool>? statExists = null)
    {
        sums++;
        return SumValues(Values(items, mods, local, subject, statExists, Program.GameLogicOrNull));
    }

    /// <summary>How many times this thread has called <see cref="Sum"/>. A stat-sources lookup whose inputs did not
    /// change must add none (Agents/docs/dev/performance.md, "Hot paths"): a test reads this before and after one.</summary>
    public static long SumsOnThisThread => sums;
    [ThreadStatic] static long sums;

    static Dictionary<uint, int> SumValues(IEnumerable<(uint Id, int Value)> values)
    {
        var sum = new Dictionary<uint, int>();
        var attributes = new Dictionary<uint, long>();
        foreach (var (id, value) in values)
            if (AttributeModifiers.IsInput(id)) attributes[id] = attributes.GetValueOrDefault(id) + value;
            else sum[id] = ArcaneMight.Combine(id, sum.GetValueOrDefault(id), value);
        // This int API is a bounded view for existing stat consumers, not an attribute calculation source.
        foreach (var (id, value) in attributes) sum[id] = AttributeModifiers.Signed(value);
        return sum;
    }

    static IEnumerable<(uint Id, int Value)> Values(IEnumerable<Item> items, ModCatalog mods, bool local,
        StatConditions.Subject subject, Func<string, bool>? statExists, IGameLogic? game)
    {
        // Influence thresholds count the full active equipment set supplied by the caller.
        var equipment = items as IReadOnlyCollection<Item> ?? items.ToArray();
        var influencedPool = subject.InfluencedEquipment ?? equipment;
        foreach (var item in equipment)
            foreach (var value in Values(item, mods, local, subject, statExists, game))
            {
                if (local || !InfluencedItemConditions.Read(Program.Stats.Name(value.Id), out string target, out var influence, out int required))
                    yield return value;
                else if (influencedPool.Count(equipped => (equipped.Influences & influence) != 0) >= required)
                    foreach (uint stat in InfluencedItemConditions.Targets(target)) yield return (stat, value.Value);
            }
    }

    /// <summary>One item's stats by Stats.dat Id, local or global. An explicit modifier's values include the
    /// item's own increased effect for its list (all, prefix or suffix); the effect stats themselves stay raw, so
    /// they never scale each other. Implicits and enchants follow the module's ModifierValueRule (their own
    /// magnitude stats); the other lists are unchanged.
    /// <para>A stat whose id carries a condition only counts while that condition holds (issue #504); with no
    /// <paramref name="subject"/> every condition is unmodelled and no stat changes.</para></summary>
    public static IEnumerable<(uint Id, int Value)> Values(Item item, ModCatalog mods, bool local = false,
        StatConditions.Subject subject = default, Func<string, bool>? statExists = null)
        => Values(item, mods, local, subject, statExists, Program.GameLogicOrNull);

    static IEnumerable<(uint Id, int Value)> Values(Item item, ModCatalog mods, bool local,
        StatConditions.Subject subject, Func<string, bool>? statExists, IGameLogic? game)
    {
        bool sourceBound = subject.ItemValue is not null;
        if (subject.ItemValue is { } itemValue)
        {
            var characterValue = subject.Value;
            subject = subject with { Value = key => itemValue(item, key) ?? characterValue?.Invoke(key), ItemValue = null };
        }
        var effect = Effect(item, mods);
        var rule = game?.ItemModifierValues(item, mods);
        foreach (var mod in item.Mods)
            if (mods.ByHash(mod.Hash) is { } row)
            {
                // A mod that displays an aura for other creatures ("Nearby Enemies have 50% increased Fire and Cold
                // Resistances") carries that aura's stats in the same row; they belong to the nearby enemies or allies,
                // never to the wearer (Eye of Malice, The Admiral). No seeded unique mixes one with self stats.
                // Where the mod's own aura template says who it reaches, that decides for the character's sum: "You and
                // nearby Allies ..." stays with the wearer, "Nearby Allies ..." does not (Dying Breath, Kingmaker). The
                // display-stat marker decides for the rest, and for an item's local sum as before.
                if (!local && row.AuraForSelf is bool self) { if (!self) continue; }
                else if (row.Stats.Any(IsOthersAura)) continue;
                long percent = EffectPercent(mod, row, effect);
                // Eldritch "while a Pinnacle Boss is in your Presence": a mod carrying the condition stat only
                // reaches the character while a Pinnacle Boss is in presence. The condition stat itself is local,
                // so it never reaches this global sum; the gate is on the mod, decided here — the one place every
                // global effect stat of the mod passes through. Its predicate is the client's PinnacleAtlasBoss
                // marker mod on the monster's variety (Game.CelestialBossInPresenceLocked). A subject that does
                // not answer presence leaves the mod as before. Local sums are not gated: Local() caches per item
                // and takes no subject.
                if (!local && RequiresCelestialPresence(row, mod) && !CelestialBossInPresence(subject)) continue;
                // The same gate for "while a Unique Enemy is in your Presence" (Stats 15801, 1,362 Mods rows).
                if (!local && RequiresUniquePresence(row, mod) && !UniqueEnemyInPresence(subject)) continue;
                for (int s = 0; s < row.Stats.Count && s < mod.Values.Count; s++)
                    if (row.Stats[s].Local == local
                        || !local && sourceBound && StatConditions.SourceLocalCondition(row.Stats[s].Name))
                    {
                        uint id = row.Stats[s].Id;
                        string name = row.Stats[s].Name;
                        var state = StatConditions.Of(name, subject);
                        // Unknown source relationships retain the local view instead of leaking into a global one.
                        if (row.Stats[s].Local && !local && state is StatConditions.State.NotModelled) continue;
                        if (!StatConditions.Counts(state))
                        {
                            // Its rule records the event its condition waits for: the id stays, with a value nothing reads.
                            if (StatConditions.KeepsOwnId(name)) yield return (id, 1);
                            continue;
                        }
                        // The condition holds: a conditional stat with no consumer of its own moves onto its plain
                        // name, which is what the pools read (issue #504).
                        // An unscalable number stays as rolled (ModScaling); asked only when something would scale it.
                        int value = IsEffectStat(row.Stats[s].Id) || (percent != 0 || rule is not null) && ModScaling.Unscalable(row, s)
                            ? mod.Values[s]
                            : rule?.Invoke(mod, row, s, percent) ?? Scale(mod.Values[s], percent);
                        value = StatConditions.Counted(name, value, state, subject, statExists, out var targets);
                        if (targets is null) yield return (id, value);
                        else
                        {
                            foreach (string target in targets) yield return (Stat.Of(target), value);
                            if (StatConditions.KeepsOwnId(name)) yield return (id, 1);
                        }
                    }
            }
    }

    /// <summary>The item's own increased-effect stats, summed raw: explicit (every explicit modifier), prefix and
    /// suffix (only that side). The stats are local, so they are read here rather than from the character's list.</summary>
    static (long Explicit, long Prefix, long Suffix) Effect(Item item, ModCatalog mods)
    {
        long explicitAll = 0, prefix = 0, suffix = 0;
        foreach (var mod in item.Mods)
            if (mods.ByHash(mod.Hash) is { } row)
                for (int s = 0; s < row.Stats.Count && s < mod.Values.Count; s++)
                    switch (row.Stats[s].Id)
                    {
                        case ExplicitEffect: explicitAll += mod.Values[s]; break;
                        case PrefixEffect: prefix += mod.Values[s]; break;
                        case SuffixEffect: suffix += mod.Values[s]; break;
                    }
        return (explicitAll, prefix, suffix);
    }

    /// <summary>How much the item's effect stats raise this modifier: only explicit affixes are affected, by the
    /// all-explicit stat plus the stat for their own side.</summary>
    static long EffectPercent(ItemMod mod, ModCatalog.Mod row, (long Explicit, long Prefix, long Suffix) effect) =>
        mod.Section != ModSection.Explicit ? 0
        : effect.Explicit + (row.GenerationType == ItemRolls.Prefix ? effect.Prefix
            : row.GenerationType == ItemRolls.Suffix ? effect.Suffix : 0);

    /// <summary>Whether a stat marks its mod as an aura for other creatures. The marker is a display stat: "nearby"
    /// (enemies or allies) or a debuff the wearer puts on them.</summary>
    static bool IsOthersAura(ModCatalog.ModStat stat) => stat.Name is var id && id.StartsWith("local_display_", StringComparison.Ordinal)
        && !StatConditions.SourceLocalCondition(id)
        && (id.Contains("nearby", StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("_debuff", StringComparison.OrdinalIgnoreCase));

    static bool IsEffectStat(uint id) => id is ExplicitEffect or PrefixEffect or SuffixEffect;

    /// <summary>The Eldritch condition stat (Stats 15802, IsLocal): a mod carrying it with a nonzero value only
    /// applies while a Pinnacle Boss is in the wearer's presence (1,362 Mods rows, value 1).</summary>
    const uint RequiresCelestialBossPresence = Stat.LocalInfluenceModRequiresCelestialBossPresence;

    static bool RequiresCelestialPresence(ModCatalog.Mod row, ItemMod mod)
    {
        for (int s = 0; s < row.Stats.Count && s < mod.Values.Count; s++)
            if (row.Stats[s].Id == RequiresCelestialBossPresence && mod.Values[s] != 0) return true;
        return false;
    }

    const uint RequiresUniqueMonsterPresence = Stat.LocalInfluenceModRequiresUniqueMonsterPresence;

    static bool RequiresUniquePresence(ModCatalog.Mod row, ItemMod mod)
    {
        for (int s = 0; s < row.Stats.Count && s < mod.Values.Count; s++)
            if (row.Stats[s].Id == RequiresUniqueMonsterPresence && mod.Values[s] != 0) return true;
        return false;
    }

    /// <summary>Whether the subject says a Unique Enemy is in presence; true when the subject does not answer it.</summary>
    static bool UniqueEnemyInPresence(StatConditions.Subject subject) =>
        subject.Flags is null || !subject.Flags.TryGetValue(StatConditions.UniqueEnemyInPresence, out bool present) || present;

    /// <summary>Whether the subject says a Pinnacle Boss is in presence; true when the subject does not answer it.</summary>
    static bool CelestialBossInPresence(StatConditions.Subject subject) =>
        subject.Flags is null || !subject.Flags.TryGetValue(StatConditions.CelestialBossInPresence, out bool present) || present;

    /// <summary>The game's increased-effect multiplication on one integer value, rounded down like the Adorned's
    /// jewel effect (Items.AdornedJewel).</summary>
    static int Scale(int value, long percent) => percent == 0 ? value
        : (int)Math.Clamp(Math.Floor((decimal)value * (100 + percent) / 100), int.MinValue, int.MaxValue);

    /// <summary>Wide per-key attribute totals: retain them until complete raw-key replacement and bucket scaling.</summary>
    public static Dictionary<uint, long> RawAttributes(IEnumerable<Item> items, ModCatalog mods,
        StatConditions.Subject subject = default, Func<string, bool>? statExists = null)
    {
        var raw = new Dictionary<uint, long>();
        foreach (var (id, value) in Values(items, mods, false, subject, statExists, Program.GameLogicOrNull))
            if (AttributeModifiers.IsInput(id)) raw[id] = raw.GetValueOrDefault(id) + value;
        return raw;
    }

    /// <summary>The character's stats from these equipped items, by client stat key (Stats.dat row + 1).
    /// <paramref name="subject"/> gates the condition-carrying stats (issue #504); without one nothing changes. A
    /// conditional stat that holds moves onto its plain name (the catalog answers which names exist).</summary>
    public static Dictionary<uint, int> Global(IEnumerable<Item> equipped, ModCatalog mods, StatCatalog stats,
        StatConditions.Subject subject = default)
    {
        var items = equipped.ToArray();
        var game = Program.GameLogicOrNull;
        var result = new Dictionary<uint, int>();
        foreach (var (id, value) in SumValues(Values(items, mods, local: false,
            subject: subject, statExists: stats.Has, game: game)))
            if (StatCatalog.IsClientId(id)) result[id] = result.GetValueOrDefault(id) + value;
        // PoB Item.lua leaves ItemCondition-tagged increases in the character mod list, outside calcLocal.
        // This stat is marked IsLocal in Stats.dat, but affects global ES only while its source item is corrupted.
        int conditionalES = SumValues(Values(items.Where(i => i.Corrupted), mods, local: true,
            subject: default, statExists: null, game: game))
            .GetValueOrDefault(Stat.LocalMaximumEnergyShieldPlusPctIfItemCorrupted);
        if (conditionalES != 0 && Stat.MaximumEnergyShieldPlusPct is var esKey)
            result[esKey] = (int)Math.Clamp((long)result.GetValueOrDefault(esKey) + conditionalES, int.MinValue, int.MaxValue);
        return result;
    }

    /// <summary>One item's local stats by Stats.dat Id (local_critical_strike_chance_+% …).</summary>
    sealed record LocalValues(long Revision, Dictionary<uint, int> Stats);
    sealed class LocalItems
    {
        public readonly System.Runtime.CompilerServices.ConditionalWeakTable<Item, LocalValues> Items = new();
    }
    sealed class LocalRules
    {
        public readonly LocalItems Default = new();
        public readonly System.Runtime.CompilerServices.ConditionalWeakTable<IGameLogic, LocalItems> Games = new();
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModCatalog, LocalRules> LocalCatalogs = new();

    // Return a copy: callers merge their own values into these dictionaries.
    public static Dictionary<uint, int> Local(Item item, ModCatalog mods) => new(LocalCached(item, mods));

    /// <summary>One local stat without Local's copy, for read-only checks on hot paths.</summary>
    public static int LocalValue(Item item, ModCatalog mods, uint stat) => LocalCached(item, mods).GetValueOrDefault(stat);

    static Dictionary<uint, int> LocalCached(Item item, ModCatalog mods)
    {
        // A replacement game supplies a new rule generation. Weak keys let its old load context be collected.
        var game = Program.GameLogicOrNull;
        var rules = LocalCatalogs.GetOrCreateValue(mods);
        var cache = (game is null ? rules.Default : rules.Games.GetOrCreateValue(game)).Items;
        long revision = mods.Revision;
        if (cache.TryGetValue(item, out var existing) && existing.Revision == revision)
            return existing.Stats;
        var stats = SumValues(Values(item, mods, local: true, subject: default, statExists: null, game: game));
        cache.AddOrUpdate(item, new LocalValues(revision, stats));
        return stats;
    }

    /// <summary>Local conditional rows need the current actor's subject and cannot use the immutable item cache.</summary>
    public static Dictionary<uint, int> Local(Item item, ModCatalog mods, StatConditions.Subject subject,
        Func<string, bool> statExists) => Sum([item], mods, local: true, subject: subject, statExists: statExists);

    /// <summary>A shield's block chance: the base type's from ShieldTypes.dat plus the shield's own local added block
    /// ("+X% Chance to Block" is a local modifier). A local no-block modifier (Kongming's Stratagem) removes it.</summary>
    public static int ShieldBlock(ItemCatalog items, Item item, ItemCatalog.BaseType type)
    {
        var local = Local(item, items.Mods);
        return local.GetValueOrDefault(Stat.LocalNoBlockChance) != 0 ? 0
            : items.ShieldBlock(type) + local.GetValueOrDefault(Stat.LocalAdditionalBlockChancePct);
    }

    /// <summary>An item's attribute requirements after its own local modifiers: the base's requirement raised or lowered
    /// by <c>local_attribute_requirements_+%</c> and the per-attribute stats (Agnerod's 40% increased Strength
    /// Requirement, Chober Chaber's reduced one), or zero with a no-attribute-requirement modifier. A row that is both the
    /// base implicit and an explicit mod counts once (Queen's Escape).</summary>
    public static (int Str, int Dex, int Int) AttributeRequirements(ItemCatalog items, Item item, ItemCatalog.BaseType type)
    {
        var local = Local(item, items.Mods);
        if (local.GetValueOrDefault(Stat.LocalNoAttributeRequirements) != 0
            || local.GetValueOrDefault(Stat.LocalUniqueTabulaRasaNoRequirementOrEnergyShield) != 0) return (0, 0, 0);
        int Percent(uint id) => item.Mods.GroupBy(mod => mod.Hash).Sum(group => ValueOf(group.First(), id));
        int ValueOf(ItemMod mod, uint id)
        {
            if (items.Mods.ByHash(mod.Hash) is not { } row) return 0;
            for (int s = 0; s < row.Stats.Count && s < mod.Values.Count; s++)
                if (row.Stats[s].Id == id) return mod.Values[s];
            return 0;
        }
        int all = Percent(Stat.LocalAttributeRequirementsPlusPct);
        int Scale(int value, int percent) => (int)Math.Floor(value * (100L + all + percent) / 100.0);
        var (str, dex, intel) = items.Requirements(type);
        // "+# Strength Requirement" (Mjölner's +300, Cospri's Malice): a flat amount on top, never below none.
        int Flat(int value, uint id) => Math.Max(0, value + Percent(id));
        return (Flat(Scale(str, Percent(Stat.LocalStrengthRequirementPlusPct)), Stat.LocalStrengthRequirementPlus),
            Flat(Scale(dex, Percent(Stat.LocalDexterityRequirementPlusPct)), Stat.LocalDexterityRequirementPlus),
            Flat(Scale(intel, Percent(Stat.LocalIntelligenceRequirementPlusPct)), Stat.LocalIntelligenceRequirementPlus));
    }

    /// <summary>A worn item with what it needs: strength, dexterity, intelligence and character level.</summary>
    public sealed record Worn(uint Id, Item Item, int Strength, int Dexterity, int Intelligence, int Level)
    {
        public bool BaseRequirementsApplied { get; init; }
    }

    /// <summary>Apply clothing's attribute waiver before merging the socketed gems' requirements.</summary>
    public static Worn ApplyBaseRequirementOverrides(Worn worn, ModCatalog mods)
    {
        if (worn.BaseRequirementsApplied) return worn;
        var local = Local(worn.Item, mods);
        bool waived = local.GetValueOrDefault(Stat.LocalNoAttributeRequirements) != 0
            || local.GetValueOrDefault(Stat.LocalUniqueTabulaRasaNoRequirementOrEnergyShield) != 0;
        return worn with { Strength = waived ? 0 : worn.Strength, Dexterity = waived ? 0 : worn.Dexterity,
            Intelligence = waived ? 0 : worn.Intelligence, BaseRequirementsApplied = true };
    }

    /// <summary>
    /// The worn items that are inactive (their requirements are not met), as in the game: an item stays equipped but its
    /// mods and socketed gems do nothing. It works step by step from <paramref name="previous"/> (the inactive items
    /// before this change; ids no longer worn are dropped):
    /// <list type="number">
    /// <item>An inactive item comes back when the character (<paramref name="baseAttributes"/>: class and passives), the
    /// active items and the level meet its requirement; its own attributes do not count while it is off.</item>
    /// <item>Then, with the attributes of every active item counted (its own too: the client only lets an item be put
    /// on when its requirements are met without it, so a worn active item has qualified), one item that falls short is
    /// switched off, the totals are counted again, and so on until every active item is satisfied.</item>
    /// </list>
    /// Taking off a +30 strength ring that another +30 strength item needed leaves that item on if the rest still
    /// reaches its requirement; an item it switches off can switch off more.
    /// </summary>
    public static (HashSet<uint> Inactive, IReadOnlyList<Item> Active) ResolveEquipment(
        IReadOnlyList<Worn> worn, (int Str, int Dex, int Int) baseAttributes, int level, ModCatalog mods,
        IReadOnlySet<uint>? previous = null)
        => ResolveEquipment(worn, level, active =>
        {
            var raw = RawAttributes(active.Select(w => w.Item), mods);
            return AttributeModifiers.FromWide(name => raw.GetValueOrDefault(name))
                .Apply(baseAttributes.Str, baseAttributes.Dex, baseAttributes.Int);
        }, previous, mods);

    /// <summary>Recalculate from active items using the same staged requirement policy.</summary>
    public static (HashSet<uint> Inactive, IReadOnlyList<Item> Active) ResolveEquipment(
        IReadOnlyList<Worn> worn, int level, ModCatalog mods,
        Func<IReadOnlyList<Item>, (int Str, int Dex, int Int)> totalsOfActive,
        IReadOnlySet<uint>? previous = null) => ResolveEquipment(worn, level,
            active => totalsOfActive(active.Select(w => w.Item).ToArray()), previous, mods);

    /// <summary>Resolve the same staged policy with final attributes recalculated from the active raw sources.</summary>
    public static (HashSet<uint> Inactive, IReadOnlyList<Item> Active) ResolveEquipment(
        IReadOnlyList<Worn> worn, int level, Func<IReadOnlyList<Worn>, (int Str, int Dex, int Int)> finalAttributes,
        IReadOnlySet<uint>? previous = null, ModCatalog? mods = null)
    {
        if (mods is not null) worn = worn.Select(w => ApplyBaseRequirementOverrides(w, mods)).ToArray();
        var inactive = Inactive(worn, level, finalAttributes, previous);
        return (inactive, worn.Where(w => !inactive.Contains(w.Id)).Select(w => w.Item).ToArray());
    }

    public static HashSet<uint> Inactive(IReadOnlyList<Worn> worn, (int Str, int Dex, int Int) baseAttributes, int level,
        Func<Item, (int Str, int Dex, int Int)> attributesOf, IReadOnlySet<uint>? previous = null)
    {
        var own = worn.ToDictionary(w => w.Id, w => attributesOf(w.Item));
        return Inactive(worn, level, active => new AttributeModifiers(
            active.Sum(w => (long)own[w.Id].Str),
            active.Sum(w => (long)own[w.Id].Dex),
            active.Sum(w => (long)own[w.Id].Int))
                .Apply(baseAttributes.Str, baseAttributes.Dex, baseAttributes.Int), previous);
    }

    public static HashSet<uint> Inactive(IReadOnlyList<Worn> worn, int level,
        Func<IReadOnlyList<Worn>, (int Str, int Dex, int Int)> finalAttributes, IReadOnlySet<uint>? previous = null)
    {
        var inactive = worn.Where(w => previous?.Contains(w.Id) == true).Select(w => w.Id).ToHashSet();
        (int, int, int) Totals() => finalAttributes(worn.Where(w => !inactive.Contains(w.Id)).ToArray());
        bool Meets(Worn w, (int Str, int Dex, int Int) t) => level >= w.Level && t.Str >= w.Strength && t.Dex >= w.Dexterity && t.Int >= w.Intelligence;
        // 1. Back on: met by the others (an inactive item is not in the totals).
        for (bool changed = true; changed;)
        {
            changed = false;
            if (worn.FirstOrDefault(w => inactive.Contains(w.Id) && Meets(w, Totals())) is { } back) { inactive.Remove(back.Id); changed = true; }
        }
        // 2. Off, one at a time, in equipment order, recounting after each.
        for (bool changed = true; changed;)
        {
            changed = false;
            var totals = Totals();
            if (worn.FirstOrDefault(w => !inactive.Contains(w.Id) && !Meets(w, totals)) is { } off) { inactive.Add(off.Id); changed = true; }
        }
        return inactive;
    }

    /// <summary>The attributes an item's global mods give (additional_strength, all attributes, the pairs …).</summary>
    public static (int Str, int Dex, int Int) Attributes(Item item, ModCatalog mods)
    {
        var raw = RawAttributes([item], mods);
        return AttributeModifiers.FromWide(name => raw.GetValueOrDefault(name)).Flat;
    }

    /// <summary>
    /// An item's character level requirement, as the client computes it (0x1421e6d40, stored in the Mods component
    /// +0x2bc; nothing is sent): a local_level_requirement_override wins outright; otherwise the item type's level, raised
    /// to Mods.dat Level × 4 / 5 of every mod in all five lists, plus local_level_requirement_+, at least 0. The client
    /// shows it in the tooltip but does not refuse an equip below it, so the server does.
    /// </summary>
    public static int LevelRequirement(Item item, int dropLevel, ModCatalog mods)
    {
        if (item.RequiredLevel is int recorded) return recorded;
        var local = Sum([item], mods, local: true);
        if (local.GetValueOrDefault(Stat.LocalLevelRequirementOverride) is int over and not 0) return Math.Max(0, over);
        int required = Math.Max(dropLevel, item.Mods.Select(m => (mods.ByHash(m.Hash)?.Level ?? 0) * 4 / 5).DefaultIfEmpty(0).Max());
        return Math.Max(0, required + local.GetValueOrDefault(Stat.LocalLevelRequirementPlus));
    }

    /// <summary>Two stat dictionaries added together (passives + items).</summary>
    public static Dictionary<uint, int> Plus(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        var sum = new Dictionary<uint, int>(a);
        foreach (var (k, v) in b) sum[k] = ArcaneMight.Combine(k, sum.GetValueOrDefault(k), v);
        return sum;
    }
}
