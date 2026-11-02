using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.World;

public enum SurvivorsChoiceKind { NewSkill, SkillRank, Blessing, Support, Item }

/// <summary>What a run skill does when it fires: a projectile at a target, or a herald's aura pulse around the player.</summary>
public enum SurvivorsSkillKind { Projectile, Herald }

/// <summary>
/// A run skill: the gem whose item the level-up window shows, and the projectile it casts. <paramref name="Recipe"/>
/// is the effect whose projectile recipe is used (its own: the catalogue keeps only gems with a flight).
/// </summary>
public sealed record SurvivorsSkill(string Path, string Name, DamageType Type, int Effect, int Recipe, string Metadata,
    SurvivorsSkillKind Kind = SurvivorsSkillKind.Projectile);

/// <summary>A support: the support gem the window shows, and the effect it links to a skill.</summary>
public sealed record SurvivorsSupportItem(string Path, string Name, SurvivorsSupport Support);

/// <summary>A blessing: the support gem the window shows, and the run-local effect it grants.</summary>
public sealed record SurvivorsBlessingItem(string Path, string Name, SurvivorsBlessing Blessing);

public sealed record SurvivorsChoice(int Index, SurvivorsChoiceKind Kind, int Target = 0);

public enum SurvivorsBlessing { Might, Haste, Area, Critical, Leech, Wisdom, Fortitude, Swiftness }

public enum SurvivorsSupport { AddedFire, AddedCold, AddedLightning, FasterCasting, Area, Critical, FasterProjectiles, LifeLeech }

public static class SurvivorsRules
{
    public const double VictorySeconds = 900;
    public const double SpawnIntervalSeconds = 0.5;
    public const int DefaultMaxAlive = 120;
    public const int MaxSpawnPerStep = 4;
    public const int MaxSkills = 4;
    public const int MaxRank = 8;
    public const int MaxBlessings = 6;
    public const int MaxBlessingLevel = 5;
    public const int MaxSupportsPerSkill = 5;
    public const double LevelSeconds = 120;

    public static double Progress(double elapsed) => Math.Clamp(elapsed / VictorySeconds, 0, 1);

    /// <summary>The horde: 14 alive at the start, the cap by the end (Azeroth Survivors' 14 → 220 curve).</summary>
    public static int TargetAlive(double elapsed, int maxAlive = DefaultMaxAlive) =>
        Math.Clamp((int)Math.Round(14 + Progress(elapsed) * (Math.Max(14, maxAlive) - 14)), 1, Math.Max(14, maxAlive));

    public static int UnlockedCount(double elapsed, int rosterCount) =>
        rosterCount <= 0 ? 0 : Math.Clamp(1 + (int)(Progress(elapsed) * rosterCount), 1, rosterCount);

    /// <summary>A run skill's interval: 1.2 s, 7% shorter per rank, floored at 200 ms.</summary>
    public static double SkillInterval(int rank) => Math.Max(0.2, 1.2 * Math.Pow(0.93, Math.Max(0, rank - 1)));

    /// <summary>A run skill's hit: the area's base damage, +30% per rank above the first.</summary>
    public static int SkillDamage(int baseDamage, int rank) =>
        Math.Max(1, (int)(Math.Max(1, baseDamage) * (1 + 0.30 * Math.Max(0, rank - 1))));

    /// <summary>A run skill's reach: +8% radius per rank, before the run's area blessings and supports.</summary>
    public static double SkillRadius(int rank) => 1 + 0.08 * (Math.Max(1, rank) - 1);

    /// <summary>Projectiles one cast fires: one more at ranks 3, 5 and 7 (one per target, like Azeroth Survivors).</summary>
    public static int SkillProjectiles(int rank) =>
        1 + (rank >= 3 ? 1 : 0) + (rank >= 5 ? 1 : 0) + (rank >= 7 ? 1 : 0);

    /// <summary>Monster life over a run: +75% by the end, so a late kill takes several casts.</summary>
    public static double MonsterLifeScale(double elapsed) => 1 + 0.75 * Progress(elapsed);

    /// <summary>Monster damage over a run: +90% by the end.</summary>
    public static double MonsterDamageScale(double elapsed) => 1 + 0.9 * Progress(elapsed);

    public const double Range = 34;

    /// <summary>Run experience from one kill, by rarity: two per normal kill at the run's own rate.</summary>
    public static int ExperienceForKill(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 4, MonsterRarity.Rare => 10, MonsterRarity.Unique => 50, _ => 2,
    };

    /// <summary>The run experience needed to go from <paramref name="level"/> to the next one: Vampire
    /// Survivors' curve, cheap early (5, 15, 25 … then 13 a level, then 16).</summary>
    public static int LevelRequirement(int level)
    {
        int l = Math.Max(1, level);
        return l < 20 ? 5 + (l - 1) * 10 : l < 40 ? 195 + (l - 20) * 13 : 455 + (l - 40) * 16;
    }

    public const int IncomingDamagePercent = 25;

    public static int IncomingDamage(int damage, int percent = IncomingDamagePercent) =>
        damage <= 0 ? 0 : Math.Max(1, damage * Math.Clamp(percent, 1, 100) / 100);
}

public sealed class SurvivorsRun(int maxAlive = SurvivorsRules.DefaultMaxAlive)
{
    readonly object _gate = new();
    readonly Dictionary<int, int> _skills = [];
    readonly Dictionary<int, HashSet<int>> _supports = [];
    readonly Dictionary<SurvivorsBlessing, int> _blessings = [];
    readonly HashSet<int> _items = [];

    public int MaxAlive { get; } = Math.Max(3, maxAlive);
    public double Elapsed { get; private set; }
    public int Kills { get; private set; }
    public int Level { get; private set; } = 1;
    public int Experience { get; private set; }
    public int PendingChoices { get; private set; }

    public IReadOnlyDictionary<int, int> Skills { get { lock (_gate) return new Dictionary<int, int>(_skills); } }
    public IReadOnlyDictionary<SurvivorsBlessing, int> Blessings { get { lock (_gate) return new Dictionary<SurvivorsBlessing, int>(_blessings); } }

    /// <summary>The unique items the run has taken.</summary>
    public IReadOnlySet<int> Items { get { lock (_gate) return new HashSet<int>(_items); } }

    public IReadOnlySet<int> SupportsOf(int skill)
    {
        lock (_gate) return _supports.TryGetValue(skill, out var set) ? new HashSet<int>(set) : new HashSet<int>();
    }

    public IReadOnlyList<SurvivorsChoice> Offer { get; private set; } = [];
    public bool NeedsOffer => PendingChoices > 0 && Offer.Count == 0;
    public int TargetAlive => SurvivorsRules.TargetAlive(Elapsed, MaxAlive);
    public int UnlockedCount(int rosterCount) => SurvivorsRules.UnlockedCount(Elapsed, rosterCount);

    public int BlessingLevel(SurvivorsBlessing blessing) { lock (_gate) return _blessings.GetValueOrDefault(blessing); }
    public int RankOf(int skill) { lock (_gate) return _skills.GetValueOrDefault(skill); }

    bool HasSupport(int skill, SurvivorsSupport support)
    {
        lock (_gate) return _supports.TryGetValue(skill, out var set) && set.Contains((int)support);
    }

    public double DamageMultiplier => 1 + 0.12 * BlessingLevel(SurvivorsBlessing.Might);
    public double CastSpeedMultiplier => 1 + 0.10 * BlessingLevel(SurvivorsBlessing.Haste);
    public double AreaMultiplier => 1 + 0.10 * BlessingLevel(SurvivorsBlessing.Area);
    public double ProjectileSpeedMultiplier => 1 + 0.10 * BlessingLevel(SurvivorsBlessing.Swiftness);
    public int CriticalChance => 5 * BlessingLevel(SurvivorsBlessing.Critical);
    public int LeechPerKill => 2 * BlessingLevel(SurvivorsBlessing.Leech);
    public int ExperiencePercent => 100 + 10 * BlessingLevel(SurvivorsBlessing.Wisdom);
    public int IncomingDamagePercent =>
        Math.Max(5, SurvivorsRules.IncomingDamagePercent - 2 * BlessingLevel(SurvivorsBlessing.Fortitude));

    public double SkillDamageMultiplier(int skill)
    {
        double more = 1;
        foreach (int support in SupportsOf(skill))
            if (support is (int)SurvivorsSupport.AddedFire or (int)SurvivorsSupport.AddedCold
                or (int)SurvivorsSupport.AddedLightning) more *= 1.25;
        return DamageMultiplier * more;
    }

    public double SkillCastMultiplier(int skill) =>
        CastSpeedMultiplier * (HasSupport(skill, SurvivorsSupport.FasterCasting) ? 1.25 : 1);
    public double SkillAreaMultiplier(int skill) =>
        AreaMultiplier * (HasSupport(skill, SurvivorsSupport.Area) ? 1.3 : 1);
    public double SkillProjectileSpeedMultiplier(int skill) =>
        ProjectileSpeedMultiplier * (HasSupport(skill, SurvivorsSupport.FasterProjectiles) ? 1.3 : 1);
    public int SkillCriticalChance(int skill) =>
        CriticalChance + (HasSupport(skill, SurvivorsSupport.Critical) ? 10 : 0);
    public int SkillLeechPerKill(int skill) =>
        LeechPerKill + (HasSupport(skill, SurvivorsSupport.LifeLeech) ? 3 : 0);

    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        lock (_gate) Elapsed += seconds;
    }

    public int RecordKill(MonsterRarity rarity)
    {
        lock (_gate)
        {
            Kills++;
            Experience += SurvivorsRules.ExperienceForKill(rarity) * ExperiencePercent / 100;
            while (Experience >= SurvivorsRules.LevelRequirement(Level))
            {
                Experience -= SurvivorsRules.LevelRequirement(Level);
                Level++;
                PendingChoices++;
            }
            return PendingChoices;
        }
    }

    public void Learn(int skill)
    {
        lock (_gate) if (!_skills.ContainsKey(skill)) _skills[skill] = 1;
    }

    public IReadOnlyList<SurvivorsChoice> DealOffer(Random random, int skillCatalog, int blessingCatalog, int supportCatalog,
        IReadOnlyList<int>? itemPool = null)
    {
        lock (_gate)
        {
            if (PendingChoices <= 0) return [];
            if (Offer.Count > 0) return Offer;
            var offer = new List<SurvivorsChoice>();
            void Add(IEnumerable<SurvivorsChoice> pool)
            {
                var list = pool.ToList();
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (list[i], list[j]) = (list[j], list[i]);
                }
                foreach (var choice in list)
                {
                    if (offer.Count >= 3) return;
                    if (offer.Contains(choice)) continue;
                    offer.Add(choice);
                }
            }
            // A unique item every third run level, before the rest: the reference mode's gear choice.
            if (Level % 3 == 0 && itemPool is { Count: > 0 })
                Add([new SurvivorsChoice(itemPool[random.Next(itemPool.Count)], SurvivorsChoiceKind.Item)]);
            if (_skills.Count < Math.Min(SurvivorsRules.MaxSkills, skillCatalog))
                Add(Enumerable.Range(0, skillCatalog).Where(s => !_skills.ContainsKey(s))
                    .Select(s => new SurvivorsChoice(s, SurvivorsChoiceKind.NewSkill)));
            Add(SupportCandidates(supportCatalog));
            if (_blessings.Count < Math.Min(SurvivorsRules.MaxBlessings, blessingCatalog))
                Add(Enumerable.Range(0, blessingCatalog).Where(b => !_blessings.ContainsKey((SurvivorsBlessing)b))
                    .Select(b => new SurvivorsChoice(b, SurvivorsChoiceKind.Blessing)));
            if (offer.Count < 3)
                Add(_skills.Where(e => e.Value < SurvivorsRules.MaxRank).Select(e => e.Key)
                    .Select(s => new SurvivorsChoice(s, SurvivorsChoiceKind.SkillRank)));
            if (offer.Count < 3)
                Add(_blessings.Where(e => e.Value < SurvivorsRules.MaxBlessingLevel).Select(e => (int)e.Key)
                    .Select(b => new SurvivorsChoice(b, SurvivorsChoiceKind.Blessing)));
            Offer = offer;
            return offer;
        }
    }

    IEnumerable<SurvivorsChoice> SupportCandidates(int supportCatalog)
    {
        foreach (int skill in _skills.Keys.OrderBy(k => k))
        {
            var taken = _supports.GetValueOrDefault(skill) ?? [];
            if (taken.Count >= SurvivorsRules.MaxSupportsPerSkill) continue;
            for (int support = 0; support < supportCatalog; support++)
                if (!taken.Contains(support))
                    yield return new SurvivorsChoice(support, SurvivorsChoiceKind.Support, skill);
        }
    }

    public bool TryChoose(int oneBased, out SurvivorsChoice choice)
    {
        choice = default!;
        lock (_gate)
        {
            if (PendingChoices <= 0 || oneBased < 1 || oneBased > Offer.Count) return false;
            choice = Offer[oneBased - 1];
            switch (choice.Kind)
            {
                case SurvivorsChoiceKind.NewSkill:
                    if (_skills.Count >= SurvivorsRules.MaxSkills || _skills.ContainsKey(choice.Index)) return false;
                    _skills[choice.Index] = 1;
                    break;
                case SurvivorsChoiceKind.SkillRank:
                    if (!_skills.TryGetValue(choice.Index, out int rank) || rank >= SurvivorsRules.MaxRank) return false;
                    _skills[choice.Index] = rank + 1;
                    break;
                case SurvivorsChoiceKind.Support:
                    if (!_skills.ContainsKey(choice.Target)) return false;
                    if (!_supports.TryGetValue(choice.Target, out var set)) _supports[choice.Target] = set = [];
                    if (set.Count >= SurvivorsRules.MaxSupportsPerSkill || !set.Add(choice.Index)) return false;
                    break;
                case SurvivorsChoiceKind.Item:
                    // The Gateway creates and equips the unique; the run only remembers which one it took.
                    if (!_items.Add(choice.Index)) return false;
                    break;
                default:
                    var blessing = (SurvivorsBlessing)choice.Index;
                    if (!_blessings.TryGetValue(blessing, out int level)) _blessings[blessing] = 1;
                    else if (level >= SurvivorsRules.MaxBlessingLevel) return false;
                    else _blessings[blessing] = level + 1;
                    break;
            }
            PendingChoices--;
            Offer = [];
            return true;
        }
    }
}
