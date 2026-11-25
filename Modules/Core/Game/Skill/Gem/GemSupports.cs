using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;


/// <summary>Compatibility uses skill types, not the display tags printed on a gem.</summary>
public sealed class GemSupports
{
    readonly string folder;

    public GemSupports(string folder = "data")
    {
        this.folder = folder;
        _effects = Load(folder);
        _grantedLevels = LoadGrantedLevels();
        _levels = LoadLevels();
        _indexable = LoadIndexable();
    }

    public sealed record Effect(int Row, string Id, bool Support, int[] Allowed, int[] Excluded, int[] Added,
        int[] Types, int[] MinionTypes, int[] Weapons, int? Regular, bool CannotBeSupported, bool IgnoreMinions,
        bool SupportsGemsOnly)
    {
        public int[] AddedMinionTypes { get; init; } = [];
    }
    public sealed record Candidate(int Effect, int Level, int Quality, int Socket);
    public sealed record Applied(Candidate Gem, Effect Effect, uint PerLevel, int CostMultiplier,
        IReadOnlyList<(uint Key, int Value)> Stats);
    readonly Effect[] _effects;
    readonly (int Effect, int Level)[] _grantedLevels;

    (int Effect, int Level)[] LoadGrantedLevels() =>
        [.. Read("GrantedEffectsPerLevel").Select(row =>
            (row.Int("GrantedEffect"), row.Int("Level")))];
    readonly Dictionary<(int Effect, int Level), (uint Row, int Cost, int Speed)> _levels;

    Dictionary<(int Effect, int Level), (uint Row, int Cost, int Speed)> LoadLevels()
    {
        var result = new Dictionary<(int, int), (uint, int, int)>();
        uint row = 0;
        foreach (var r in Read("GrantedEffectsPerLevel"))
            result.TryAdd((r.Int("GrantedEffect"), r.Int("Level")),
                (row++, r.Int("CostMultiplier"), r.Int("AttackSpeedMultiplier")));
        return result;
    }

    public IReadOnlyList<Effect> Effects => _effects;
    public Effect? Of(int effect) => effect >= 0 && effect < _effects.Length ? _effects[effect] : null;

    /// <summary>Item supports apply to every compatible gem in this item, independently of links.
    /// Use the mod's actual granted effects, not a guessed effect based on its translated tooltip.</summary>
    public IEnumerable<Candidate> ItemCandidates(Item host, ModCatalog mods)
    {
        int source = host.Sockets.Count;
        foreach (var itemMod in host.Mods)
        {
            if (mods.ByHash(itemMod.Hash) is not { } mod) continue;
            var grants = mod.GrantedEffectLevels.Where(row => row >= 0 && row < _grantedLevels.Length)
                .Select(row => _grantedLevels[row])
                .Where(row => Of(row.Effect) is { Support: true }).ToArray();
            // A developer can change a numeric support level with /mod set. Fixed-level display flags encode
            // the level in their stat name and keep the native granted-effect level, as do non-level pseudo-supports.
            var levelStats = mod.Stats.Select((stat, index) => (stat.Id, Index: index))
                .Where(s => s.Index < itemMod.Values.Count && IsVariableSupportLevel(Stats.Name(s.Id))).ToArray();
            foreach (var grant in grants)
            {
                int level = grant.Level;
                if (grants.Length == 1 && levelStats.Length == 1) level = itemMod.Values[levelStats[0].Index];
                if (level > 0) yield return new(grant.Effect, level, 0, source++);
            }
            // Forbidden Shako: the mod rolls which support it grants ("Socketed Gems are Supported by Level {0} {1}",
            // display_indexable_support): local_random_support_gem_index[_1] is an IndexableSupportGems Index, and the
            // local_random_support_gem_level stat with the same suffix its level.
            for (int i = 0; i < mod.Stats.Count && i < itemMod.Values.Count; i++)
            {
                string statName = Stats.Name(mod.Stats[i].Id);
                if (!statName.StartsWith(RandomSupportIndex, StringComparison.Ordinal)) continue;
                string levelStat = RandomSupportLevel + statName[RandomSupportIndex.Length..];
                uint? levelKey = Stats.Key(levelStat);
                if (levelKey is null) continue;
                int at = Enumerable.Range(0, Math.Min(mod.Stats.Count, itemMod.Values.Count))
                    .FirstOrDefault(j => mod.Stats[j].Id == levelKey.Value, -1);
                if (at >= 0 && itemMod.Values[at] > 0 && RandomSupportEffect(itemMod.Values[i]) is int effect)
                    yield return new(effect, itemMod.Values[at], 0, source++);
            }
        }
    }

    static bool IsVariableSupportLevel(string name)
    {
        const string fixedPrefix = "local_display_supported_by_level_";
        return name.StartsWith("local_display_", StringComparison.Ordinal) && name.Contains("level", StringComparison.Ordinal)
            && !(name.Length > fixedPrefix.Length && name.StartsWith(fixedPrefix, StringComparison.Ordinal)
                && char.IsAsciiDigit(name[fixedPrefix.Length]));
    }

    const string RandomSupportIndex = "local_random_support_gem_index", RandomSupportLevel = "local_random_support_gem_level";

    /// <summary>The support an IndexableSupportGems Index names: its SkillGems row's first GemVariants entry's
    /// GrantedEffect (the normal gem's support effect), or null for an index the table does not have.</summary>
    public int? RandomSupportEffect(int index) => _indexable.TryGetValue(index, out int effect) ? effect : null;

    readonly Dictionary<int, int> _indexable;

    Dictionary<int, int> LoadIndexable()
    {
        var gems = Read("SkillGems");
        var effects = Read("GemEffects");
        var result = new Dictionary<int, int>();
        foreach (var row in Read("IndexableSupportGems"))
            if (row.IntOrNull("Index") is int index && row.IntOrNull("SupportGem") is int gem and >= 0 && gem < gems.Count
                && gems[gem].Ints("GemVariants").FirstOrDefault(-1) is int variant and >= 0 && variant < effects.Count
                && effects[variant].IntOrNull("GrantedEffect") is int effect and >= 0)
                result.TryAdd(index, effect);
        return result;
    }

    // ActiveSkillType expressions are postfix; remaining terms are implicitly ORed (legacy table format).
    public static bool Matches(IReadOnlyList<int> expression, IReadOnlySet<int> types, bool empty)
    {
        if (expression.Count == 0) return empty;
        var stack = new Stack<bool>();
        foreach (int token in expression)
        {
            if (token == 74) { if (stack.Count < 1) return false; stack.Push(!stack.Pop()); }
            else if (token is 72 or 73)
            {
                if (stack.Count < 2) return false;
                bool right = stack.Pop(), left = stack.Pop();
                stack.Push(token == 72 ? left || right : left && right);
            }
            else stack.Push(types.Contains(token));
        }
        return stack.Any(v => v);
    }

    public IReadOnlyList<Applied> Resolve(int active, IEnumerable<Candidate> candidates, SkillCatalog skills,
        int? weaponClass = null, int activeLevel = 1, bool sourceIsGem = true) =>
        Resolve(active, candidates, skills, weaponClass, activeLevel, sourceIsGem, allOneHandMelee: false);

    internal IReadOnlyList<Applied> Resolve(int active, IEnumerable<Candidate> candidates, SkillCatalog skills,
        int? weaponClass, int activeLevel, bool sourceIsGem, bool allOneHandMelee)
    {
        if (Of(active) is not { Support: false, CannotBeSupported: false } target) return [];
        var types = target.Types.ToHashSet();
        var minionTypes = target.MinionTypes.ToHashSet();
        var all = candidates.Where(c => Of(c.Effect) is { Support: true } support
                && (sourceIsGem || !support.SupportsGemsOnly))
            .Where(c => Of(c.Effect)?.Id != "SupportCastWhenDamageTaken"
                || skills.PlayerLevelReq(active, activeLevel) <= skills.PlayerLevelReq(c.Effect, c.Level))
            .Select(c => (Gem: c, Effect: Of(c.Effect)!))
            .GroupBy(c => Family(c.Effect))
            .Select(g => g.OrderByDescending(c => c.Effect.Regular is not null).ThenByDescending(c => c.Gem.Level)
                .ThenByDescending(c => c.Gem.Quality).ThenBy(c => c.Gem.Socket).First()).ToList();
        // Type-changing supports can enable other supports. Iterate to a fixed point, independent of socket order.
        bool changed;
        do
        {
            changed = false;
            foreach (var c in all)
                if (CompatibleTypes(c.Effect, types, minionTypes, weaponClass, allOneHandMelee))
                {
                    foreach (int t in c.Effect.Added) changed |= types.Add(t);
                    foreach (int t in c.Effect.AddedMinionTypes) changed |= minionTypes.Add(t);
                }
        } while (changed);
        var result = new List<Applied>();
        foreach (var c in all.OrderBy(c => c.Gem.Socket))
            if (CompatibleTypes(c.Effect, types, minionTypes, weaponClass, allOneHandMelee)
                && Level(c.Gem.Effect, c.Gem.Level) is { } level)
                result.Add(new(c.Gem, c.Effect, level.Row, level.Cost,
                    skills.EffectStats(c.Gem.Effect, c.Gem.Level, c.Gem.Quality)));
        return result;
    }

    int Family(Effect e)
    {
        var seen = new HashSet<int>();
        while (e.Regular is int row && Of(row) is { } regular && seen.Add(row)) e = regular;
        return e.Row;
    }

    /// <summary>The game's support eligibility: the skill types (or its minions') must satisfy the support's allowed
    /// expression, must not match its excluded one, and the weapon restriction must fit.</summary>
    public static bool Compatible(Effect support, IReadOnlySet<int> types, int[] minionTypes, int? weapon) =>
        CompatibleTypes(support, types, minionTypes.ToHashSet(), weapon);

    static bool CompatibleTypes(Effect support, IReadOnlySet<int> types, IReadOnlySet<int> minionTypes, int? weapon,
        bool allOneHandMelee = false) =>
        (Matches(support.Allowed, types, true) || !support.IgnoreMinions && minionTypes.Count > 0
            && Matches(support.Allowed, minionTypes, false))
        && !Matches(support.Excluded, types, false)
        && WeaponIdentity.Allows(support.Weapons, weapon, allOneHandMelee);

    (uint Row, int Cost, int Speed)? Level(int effect, int level)
    {
        if (_levels.TryGetValue((effect, level), out var exact)) return exact;
        var rows = _levels.Where(kv => kv.Key.Effect == effect).OrderBy(kv => Math.Abs(kv.Key.Level - level));
        return rows.Any() ? rows.First().Value : null;
    }

    public double AttackSpeedMultiplier(int effect, int level) =>
        Level(effect, level) is { } v ? Math.Max(.01, 1 + v.Speed / 100.0) : 1;

    public static int LinkGroup(Item host, int socket)
    {
        int start = 0, group = 0;
        foreach (int count in host.Links ?? [])
        {
            if (socket >= start && socket < start + count) return group;
            start += count;
            group++;
        }
        return group + socket - start; // sockets without explicit groups are separate, never all linked
    }

    static Effect[] Load(string folder)
    {
        var active = Read("ActiveSkills");
        return Read("GrantedEffects").Select((r, row) =>
        {
            ClientRow? skill = r.IntOrNull("ActiveSkill") is int key && key >= 0 && key < active.Count ? active[key] : null;
            return new Effect(row, r.Text("Id")!, r.Bool("IsSupport"),
                Ints(r, "AllowedActiveSkillTypes"), Ints(r, "ExcludedActiveSkillTypes"), Ints(r, "AddedActiveSkillTypes"),
                Ints(skill, "ActiveSkillTypes"), Ints(skill, "MinionActiveSkillTypes"), Ints(r, "SupportWeaponRestrictions"),
                r.IntOrNull("RegularVariant"),
                r.Bool("CannotBeSupported"), r.Bool("IgnoreMinionTypes"), r.Bool("SupportsGemsOnly")) { AddedMinionTypes = Ints(r, "AddedMinionActiveSkillTypes") };
        }).ToArray();
    }

    static int[] Ints(ClientRow? r, string name) => r?.Ints(name) ?? [];

    static IReadOnlyList<ClientRow> Read(string table) => ClientTables.TableOrNull(table) ?? (IReadOnlyList<ClientRow>)[];
}
