using GameServer.Core.Protocol.Instance;

using GameServer.Core.World;

namespace GameServer.Modules.Survivors;

public enum SurvivorsChoiceKind { NewSkill, SkillRank, Blessing, Support, Item, Heal }

/// <summary>What a run skill does when it fires: a projectile at a target, a herald's aura pulse around the player,
/// or an area gem's nova pulse around the player.</summary>
public enum SurvivorsSkillKind { Projectile, Herald, Area }

/// <summary>
/// A run skill: the gem whose item the level-up window shows, and the projectile it casts. <paramref name="Recipe"/>
/// is the effect whose projectile recipe is used (its own: the catalogue keeps only gems with a flight).
/// </summary>
public sealed record SurvivorsSkill(string Path, string Name, DamageType Type, int Effect, int Recipe, string Metadata,
    SurvivorsSkillKind Kind = SurvivorsSkillKind.Projectile)
{
    /// <summary>The gem's own cast time (GrantedEffects CastTime), the base the run's interval starts from.</summary>
    public int CastTimeMs { get; init; } = 1000;

    /// <summary>True for an attack gem: its hit is weapon damage times the gem's damage effectiveness, not spell damage.</summary>
    public bool Attack { get; init; }

    /// <summary>True when the gem's own use is centred on the caster (Ice Nova, Blade Vortex): the run's pulse stays on
    /// the player. A ground-targeted spell (Firestorm, Bladefall) casts on the nearest monster instead.</summary>
    public bool SelfTargeted { get; init; }

    /// <summary>True for a Nova: a ring the client draws around the caster even when the row also allows a target.</summary>
    public bool Nova { get; init; }
}

/// <summary>A support: the support gem the window shows, its GrantedEffects row, and the run-local effect it links.
/// <paramref name="Herald"/> marks the pulse supports a Herald can take; every other support makes no sense on the
/// real gem's buff and the run's synthetic pulse.</summary>
public sealed record SurvivorsSupportItem(string Path, string Name, SurvivorsSupport Support, int Effect = -1,
    bool Herald = false);

/// <summary>A blessing: the support gem the window shows, and the run-local effect it grants.</summary>
public sealed record SurvivorsBlessingItem(string Path, string Name, SurvivorsBlessing Blessing);

public sealed record SurvivorsChoice(int Index, SurvivorsChoiceKind Kind, int Target = 0);

public enum SurvivorsBlessing { Might, Haste, Area, Critical, Leech, Wisdom, Fortitude, Swiftness }

public enum SurvivorsSupport
{
    AddedFire, AddedCold, AddedLightning, FasterCasting, Area, Critical, FasterProjectiles, LifeLeech,
    /// <summary>A support the catalogue now offers, beyond the eight the mode started with: its own gem stats drive it.</summary>
    Other,
}

public static class SurvivorsRules
{
    // The reference mode's clock and horde (Azeroth Survivors, crates/benilla-app/src/survivors/data.rs, enemies.rs,
    // read 2026-11-03): a 15-minute run, a boss every 135 s, a 14 → 220 horde refilled every 0.12 s, +75% monster life
    // and +90% monster damage over the run. The lore-boss gate keeps one boss per 120 s area; the ladder, not the wall
    // clock, carries the pressure curve.
    public const double VictorySeconds = 900;
    public const double BossEverySeconds = 60;
    public const double SpawnIntervalSeconds = 0.12;
    public const int DefaultMaxAlive = 220;
    public const int MaxSpawnPerStep = 4;
    public const int SpawnDivisor = 6;
    public const int MaxSkills = 6;
    public const int MaxRank = 8;
    public const int MaxBlessings = 6;
    public const int MaxBlessingLevel = 5;
    public const int MaxSupportsPerSkill = 5;
    /// <summary>Choices the level-up window offers at once.</summary>
    public const int MaxOffers = 6;

    /// <summary>
    /// How many run skills a stage allows: one per location for the first five (the first location one, the second two,
    /// …), five at most, so after the fifth location only supports and ranks grow.
    /// </summary>
    public static int SkillCap(int stage) => Math.Clamp(stage + 1, 1, 5);
    public const double LevelSeconds = 120;

    // The reference's passive values (data.rs PASSIVES): Might +10% damage, Intellect +8% cast speed, Amplify +10%
    // area, Kings +5 percentage points of base crit, Wisdom +8% experience. Detect (+40% pickup radius), Renew
    // (+0.5 life a second), Wild (+1 armour) and Pack (+10% movement) do not map onto a server-owned PoE character;
    // Fortitude's +20% maximum life a level is mapped to 1/1.2 less damage taken a level.
    public const int MightPerLevel = 10;
    public const int HastePerLevel = 8;
    public const int AreaPerLevel = 10;
    public const int CriticalPerLevelPermyriad = 500;
    public const int WisdomPerLevel = 8;
    public const int LeechPerLevel = 20;
    public const int SwiftnessPerLevel = 10;

    /// <summary>The reference's boss ladder, measured against the tier pool the player actually faces at that boss:
    /// 700 HP over the 26 HP top of the 135 s pool (26.92×), then 25.71, 49.41, 31.82, 50, 47.62 (data.rs BOSSES
    /// over wave_pool at 135 s steps). The seventh area extends it at 60×, a fight before the bell.</summary>
    static readonly double[] BossRatios = [26.92, 25.71, 49.41, 31.82, 50.0, 47.62, 60.0];

    /// <summary>The reference's boss experience (60, 120, 200, 300, 400, 500; the seventh area 600).</summary>
    static readonly int[] BossExperienceTable = [60, 120, 200, 300, 400, 500, 600];

    /// <summary>The current stage's length: <see cref="LevelSeconds"/>, or Game_SURVIVORS_STAGE_SECONDS (at least 15 s)
    /// for a verification run that does not wait two minutes per area.</summary>
    public static double StageSeconds =>
        double.TryParse(Environment.GetEnvironmentVariable("Game_SURVIVORS_STAGE_SECONDS"), out double seconds) && seconds >= 15
            ? seconds : LevelSeconds;

    public static double Progress(double elapsed) => Math.Clamp(elapsed / VictorySeconds, 0, 1);

    /// <summary>The horde: 14 alive at the start, 220 by the end (the reference's 14 → 220 linear curve).</summary>
    public static int TargetAlive(double elapsed, int maxAlive = DefaultMaxAlive) =>
        Math.Clamp((int)(14 + Progress(elapsed) * Math.Max(0, Math.Max(14, maxAlive) - 14)), 1, Math.Max(14, maxAlive));

    /// <summary>How many bodies the spawner adds this tick: a sixth of the gap, at most four (the reference's refill
    /// every 0.12 s).</summary>
    public static int SpawnRefill(int missing) =>
        Math.Clamp((int)Math.Ceiling(Math.Max(0, missing) / (double)SpawnDivisor), 0, MaxSpawnPerStep);

    public static int UnlockedCount(double elapsed, int rosterCount) =>
        rosterCount <= 0 ? 0 : Math.Clamp(1 + (int)(Progress(elapsed) * rosterCount), 1, rosterCount);

    /// <summary>
    /// The reference's sliding tier pool: the roster laid over thirty steps of the run, the spawner drawing from three
    /// consecutive tiers (data.rs <c>wave_pool</c>). The first three at the start, the last three at the bell.
    /// </summary>
    public static int[] WavePool(double elapsed, int rosterCount)
    {
        if (rosterCount <= 0) return [];
        if (rosterCount <= 3) return [.. Enumerable.Range(0, rosterCount)];
        int step = Math.Clamp((int)(Progress(elapsed) * 30.0), 0, 30);
        int span = rosterCount - 3;
        int first = step <= 1 ? 0 : Math.Clamp((step - 1) * span / 27, 0, span);
        return [first, first + 1, first + 2];
    }

    /// <summary>A run skill's interval: 1.2 s, 7% shorter per rank, floored at 200 ms.</summary>
    public static double SkillInterval(int rank) => Math.Max(0.2, 1.2 * Math.Pow(0.93, Math.Max(0, rank - 1)));

    /// <summary>The reference's rank curve: +30% damage a rank above the first (3.1× at rank 8).</summary>
    public static double SkillDamageMultiplier(int rank) => 1 + 0.30 * Math.Max(0, rank - 1);

    /// <summary>A run skill's hit: the area's base damage, +30% per rank above the first.</summary>
    public static int SkillDamage(int baseDamage, int rank) =>
        Math.Max(1, (int)(Math.Max(1, baseDamage) * SkillDamageMultiplier(rank)));

    /// <summary>The ladder rung for a boss number: the seven reference rungs, then the last one repeats.</summary>
    public static int BossLadderIndex(int bossNumber) => Math.Clamp(bossNumber, 0, BossRatios.Length - 1);

    /// <summary>The life of the area's boss at <paramref name="index"/>, as a ratio of the tier pool's top at that stage.</summary>
    public static double BossLifeRatio(int index) =>
        BossRatios[BossLadderIndex(index)];

    /// <summary>The experience a boss kill pays on top of its ordinary unique kill.</summary>
    public static int BossExperience(int index) =>
        BossExperienceTable[BossLadderIndex(index)];

    /// <summary>The gate fight's length for the stage's average build: the reference time a stage boss has always been
    /// balanced against. A stronger build kills faster; a weaker one takes longer.</summary>
    public const double BossGateSeconds = 120;

    /// <summary>The old stage boss had 105 s from its 15 s arrival to the 120 s gate, so the ladder's life ratios are
    /// calibrated to that window; the gate fight stretches the same budget to <see cref="BossGateSeconds"/>.</summary>
    public const double BossCalibrationSeconds = 105;

    /// <summary>The gate boss's life, from the tier pool top the stage draws from: the reference ladder scaled to the
    /// gate window and the run's clock. Game_SURVIVORS_BOSS_LIFE tunes the whole ladder from a playtest.</summary>
    public static int BossGateLife(int poolTop, int stage, double elapsed) =>
        Math.Max(1, (int)Math.Round(Math.Max(1, poolTop) * BossLifeRatio(stage) * MonsterLifeScale(elapsed)
            * BossGateLifeFactor * BossGateSeconds / BossCalibrationSeconds));

    /// <summary>A playtest multiplier over the reference ladder: the ladder's own window was the old 105 s stage boss,
    /// so 3 stretches a starter build's fight toward the two-minute gate; <c>Game_SURVIVORS_BOSS_LIFE</c> overrides it.</summary>
    public static double BossGateLifeFactor =>
        double.TryParse(Environment.GetEnvironmentVariable("Game_SURVIVORS_BOSS_LIFE"), out double factor) && factor > 0
            ? factor : 3;

    /// <summary>Missed gate-boss hits that kill: the base target, before the player's own defences buy their extra hits.</summary>
    public const int BossHitsToDie = 7;

    /// <summary>How many of the player's effective life the boss's hit removes (before the real mitigation pipeline):
    /// physical armour eats a fixed share of a boss-sized hit; the other types go through resistance.</summary>
    public static int BossHitDamage(int pool, int resistance, DamageType type, double multiplier = 1) =>
        Math.Max(1, (int)Math.Round(Math.Max(1, pool)
            / (BossHitsToDie * Math.Clamp(TakenFraction(resistance, type), 0.05, 1)) * Math.Max(0, multiplier)));

    /// <summary>The share of a boss hit its type leaves after the player's flat resistance (physical uses armour's
    /// approximate share at this size; the hit pipeline still applies the real armour roll).</summary>
    public static double TakenFraction(int resistance, DamageType type) => type == DamageType.Physical
        ? 0.7 : 1 - Math.Clamp(resistance, 0, 75) / 100.0;

    /// <summary>A run skill's reach: +8% radius per rank, before the run's area blessings and supports.</summary>
    public static double SkillRadius(int rank) => 1 + 0.08 * (Math.Max(1, rank) - 1);

    /// <summary>Projectiles one cast fires: one more at ranks 3, 5 and 7 (one per target, like Azeroth Survivors).</summary>
    public static int SkillProjectiles(int rank) =>
        1 + (rank >= 3 ? 1 : 0) + (rank >= 5 ? 1 : 0) + (rank >= 7 ? 1 : 0);

    /// <summary>Monster life over a run: +250% by the end, so a late kill takes several casts.</summary>
    public static double MonsterLifeScale(double elapsed) => 1 + 2.5 * Progress(elapsed);

    /// <summary>Monster damage over a run: +250% by the end, so standing still is punished.</summary>
    public static double MonsterDamageScale(double elapsed) => 1 + 2.5 * Progress(elapsed);

    /// <summary>How often the horde sends a ring of ranged monsters (archers/wands) for the player to dodge.</summary>
    public const double RangedWaveIntervalSeconds = 20;

    /// <summary>The bodies in a ranged ring: six at the start, sixteen by the bell.</summary>
    public static int RangedWaveCount(double progress) => 6 + (int)Math.Round(Math.Clamp(progress, 0, 1) * 10);

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

    public const int IncomingDamagePercent = 60;

    /// <summary>Fortitude's mapping of the reference's +20% maximum life a level: 1/1.2 less damage taken a level
    /// (60% → 50 → 42 → 35 → 29 → 24).</summary>
    public static int FortitudeIncomingPercent(int level) =>
        (int)Math.Round(IncomingDamagePercent / Math.Pow(1.2, Math.Clamp(level, 0, MaxBlessingLevel)));

    /// <summary>The next area opens only once the stage's lore boss has fallen; an area with no boss never seals.</summary>
    public static bool WaySealed(int bossCount, bool bossDefeated) => bossCount > 0 && !bossDefeated;

    public static int IncomingDamage(int damage, int percent = IncomingDamagePercent) =>
        damage <= 0 ? 0 : Math.Max(1, damage * Math.Clamp(percent, 1, 100) / 100);
}

/// <summary>
/// The Path of Building formulas a run skill uses, in the form its calc modules define them
/// (PathOfBuildingCommunity/PathOfBuilding, pinned 16de4b82: CalcOffence "calcDamage", CalcTools "calcRadius",
/// CalcTools "calcCastTime"). They are plain arithmetic so the run can be tested without the game data.
/// </summary>
public static class SurvivorsFormulas
{
    /// <summary>
    /// PoB's cast time: the gem's cast time divided by (1 + increased cast speed). The run also shortens a skill's
    /// interval 7% per rank, so a maxed skill casts fast, and never below the 200 ms floor the mode has always used.
    /// </summary>
    public static double CastInterval(double baseSeconds, int rank, int increasedCastSpeedPercent) =>
        Math.Max(0.2, Math.Max(0.05, baseSeconds) * Math.Pow(0.93, Math.Max(0, rank - 1))
            / Math.Max(0.01, 1 + increasedCastSpeedPercent / 100.0));

    /// <summary>
    /// PoB's critical strike chance: (intrinsic base + additional base) × (1 + increased / 100), in permyriad
    /// (10 000 = 100%), capped there as the client does. <paramref name="increasedPercent"/> is the summed
    /// "increased Critical Strike Chance" from the skill, its supports and the run's blessings.
    /// </summary>
    public static int CriticalChance(int basePermyriad, int addedPermyriad, int increasedPercent)
    {
        double basis = Math.Max(0, basePermyriad) + Math.Max(0, addedPermyriad);
        double chance = basis * Math.Max(0, 1 + increasedPercent / 100.0);
        return (int)Math.Clamp(Math.Floor(chance), 0, 10_000);
    }

    /// <summary>PoB's critical strike multiplier: 150% base plus the skill's/support's added percentage.</summary>
    public static int CriticalMultiplier(int addedPercent) => Math.Max(100, 150 + addedPercent);

    /// <summary>PoB's calcRadius: a radius grows with the square root of the increased area, never linearly.</summary>
    public static double AreaRadius(double baseRadius, int increasedAreaPercent) =>
        Math.Max(0, baseRadius) * Math.Sqrt(Math.Max(0, 1 + increasedAreaPercent / 100.0));

    /// <summary>PoB's projectile damage: (base + added × effectiveness) × (1 + increased) × each more multiplier.</summary>
    public static double HitDamage(double baseMin, double baseMax, double effectiveness, double addedMin, double addedMax,
        int increasedPercent, double more = 1)
    {
        double min = Math.Max(0, baseMin) + Math.Max(0, addedMin) * Math.Max(0, effectiveness);
        double max = Math.Max(0, baseMax) + Math.Max(0, addedMax) * Math.Max(0, effectiveness);
        return Math.Max(0, (min + max) / 2 * Math.Max(0, 1 + increasedPercent / 100.0) * Math.Max(0, more));
    }
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

    /// <summary>Links a support to a learned skill directly (the dev verification command); false when it cannot link.</summary>
    public bool Link(int skill, int support)
    {
        lock (_gate)
        {
            if (!_skills.ContainsKey(skill)) return false;
            if (!_supports.TryGetValue(skill, out var set)) _supports[skill] = set = [];
            return set.Count < SurvivorsRules.MaxSupportsPerSkill && set.Add(support);
        }
    }

    public IReadOnlyList<SurvivorsChoice> Offer { get; private set; } = [];
    public bool NeedsOffer => PendingChoices > 0 && Offer.Count == 0;
    public int TargetAlive => SurvivorsRules.TargetAlive(Elapsed, MaxAlive);
    public int UnlockedCount(int rosterCount) => SurvivorsRules.UnlockedCount(Elapsed, rosterCount);

    public int BlessingLevel(SurvivorsBlessing blessing) { lock (_gate) return _blessings.GetValueOrDefault(blessing); }
    public int RankOf(int skill) { lock (_gate) return _skills.GetValueOrDefault(skill); }

    /// <summary>The run's increased damage (Might), added to the gem's and its supports' own increases.</summary>
    public int DamageIncreasedPercent => SurvivorsRules.MightPerLevel * BlessingLevel(SurvivorsBlessing.Might);
    public double DamageMultiplier => 1 + DamageIncreasedPercent / 100.0;
    public int CastSpeedIncreasedPercent => SurvivorsRules.HastePerLevel * BlessingLevel(SurvivorsBlessing.Haste);
    public double CastSpeedMultiplier => 1 + CastSpeedIncreasedPercent / 100.0;
    public int AreaIncreasedPercent => SurvivorsRules.AreaPerLevel * BlessingLevel(SurvivorsBlessing.Area);
    public double AreaMultiplier => 1 + AreaIncreasedPercent / 100.0;
    public int ProjectileSpeedIncreasedPercent => SurvivorsRules.SwiftnessPerLevel * BlessingLevel(SurvivorsBlessing.Swiftness);
    public double ProjectileSpeedMultiplier => 1 + ProjectileSpeedIncreasedPercent / 100.0;
    /// <summary>Added base critical strike chance in permyriad (the reference's Kings: +5 percentage points a level).</summary>
    public int CriticalChanceAddedPermyriad => SurvivorsRules.CriticalPerLevelPermyriad * BlessingLevel(SurvivorsBlessing.Critical);
    /// <summary>Life leeched per 10 000 damage of a hit, from the Leech blessing (0.2% a level).</summary>
    public int LeechPermyriad => SurvivorsRules.LeechPerLevel * BlessingLevel(SurvivorsBlessing.Leech);
    public int ExperiencePercent => 100 + SurvivorsRules.WisdomPerLevel * BlessingLevel(SurvivorsBlessing.Wisdom);
    public int IncomingDamagePercent =>
        SurvivorsRules.FortitudeIncomingPercent(BlessingLevel(SurvivorsBlessing.Fortitude));

    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        lock (_gate) Elapsed += seconds;
    }

    public int RecordKill(MonsterRarity rarity)
    {
        lock (_gate) Kills++;
        return RecordExperience(SurvivorsRules.ExperienceForKill(rarity) * ExperiencePercent / 100);
    }

    /// <summary>Adds run experience (a kill, a boss bonus) and returns the pending choices after any level-ups.</summary>
    public int RecordExperience(int amount)
    {
        lock (_gate)
        {
            if (amount <= 0) return PendingChoices;
            Experience += amount;
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

    /// <summary>Sets a learned skill's rank (the dev verification commands): 1 to <see cref="SurvivorsRules.MaxRank"/>.</summary>
    public bool SetRank(int skill, int rank)
    {
        lock (_gate)
        {
            if (!_skills.ContainsKey(skill)) return false;
            _skills[skill] = Math.Clamp(rank, 1, SurvivorsRules.MaxRank);
            return true;
        }
    }

    /// <summary>
    /// The level-up offer. A learned skill's **missing supports come first**: six choices of the supports it does not
    /// have yet, so a skill's links can actually be completed. Rank-ups and new skills wait until every learned skill
    /// has all the supports it can take (the run teaches links before power), then the reference's weighted pool
    /// (rank 1.2, new skill 1.0, blessing rank 0.8, new blessing 0.5, a unique every third level 0.4) fills the rest.
    /// A new skill is also capped by the stage: the first location allows one skill, the second two, up to five; after
    /// the fifth only supports and ranks grow.
    /// When every slot is mastered the window offers a full heal instead, as the reference does.
    /// </summary>
    public IReadOnlyList<SurvivorsChoice> DealOffer(Random random, int skillCatalog, int blessingCatalog, int supportCatalog,
        IReadOnlyList<int>? itemPool = null, Func<int, int, bool>? linkable = null, int stage = 0)
    {
        lock (_gate)
        {
            if (PendingChoices <= 0) return [];
            if (Offer.Count > 0) return Offer;
            // A skill is complete once it holds every support it can take, capped at the five slots it has.
            bool Complete(int skill)
            {
                int taken = _supports.GetValueOrDefault(skill)?.Count ?? 0;
                if (taken >= SurvivorsRules.MaxSupportsPerSkill) return true;
                if (linkable is null) return false;
                int canTake = Enumerable.Range(0, supportCatalog).Count(support => linkable(skill, support));
                return taken >= Math.Min(SurvivorsRules.MaxSupportsPerSkill, canTake);
            }
            var offer = new List<SurvivorsChoice>();
            void Take(SurvivorsChoice choice)
            {
                if (offer.Count < SurvivorsRules.MaxOffers && !offer.Contains(choice)) offer.Add(choice);
            }
            // The supports a learned skill is missing are always offered: the player has to be able to finish a link set.
            var supports = SupportCandidates(supportCatalog, linkable).ToList();
            for (int i = supports.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (supports[i], supports[j]) = (supports[j], supports[i]);
            }
            foreach (var support in supports) Take(support);
            var pool = new List<(SurvivorsChoice Choice, double Weight)>();
            void Add(SurvivorsChoice choice, double weight)
            {
                if (offer.Contains(choice)) return;
                if (!pool.Any(p => p.Choice == choice)) pool.Add((choice, weight));
            }
            // Power waits for the links: a rank-up only once that skill is complete.
            foreach (var (skill, rank) in _skills.OrderBy(e => e.Key))
                if (rank < SurvivorsRules.MaxRank && Complete(skill))
                    Add(new SurvivorsChoice(skill, SurvivorsChoiceKind.SkillRank), 1.2);
            // A new skill waits until every learned skill is complete, and the stage caps how many the run has: the
            // first location allows one, the second two, up to five; after the fifth only supports and ranks grow.
            int skillCap = Math.Min(SurvivorsRules.SkillCap(stage), skillCatalog);
            if (_skills.Count < skillCap && _skills.Keys.All(Complete))
                foreach (int skill in Enumerable.Range(0, skillCatalog))
                    if (!_skills.ContainsKey(skill)) Add(new SurvivorsChoice(skill, SurvivorsChoiceKind.NewSkill), 1.0);
            foreach (var (blessing, level) in _blessings.OrderBy(e => e.Key))
                if (level < SurvivorsRules.MaxBlessingLevel)
                    Add(new SurvivorsChoice((int)blessing, SurvivorsChoiceKind.Blessing), 0.8);
            if (_blessings.Count < Math.Min(SurvivorsRules.MaxBlessings, blessingCatalog))
                foreach (int blessing in Enumerable.Range(0, blessingCatalog))
                    if (!_blessings.ContainsKey((SurvivorsBlessing)blessing))
                        Add(new SurvivorsChoice(blessing, SurvivorsChoiceKind.Blessing), 0.5);
            if (Level % 3 == 0 && itemPool is { Count: > 0 })
                Add(new SurvivorsChoice(itemPool[random.Next(itemPool.Count)], SurvivorsChoiceKind.Item), 0.4);
            while (offer.Count < SurvivorsRules.MaxOffers && pool.Count > 0)
            {
                double total = pool.Sum(p => p.Weight);
                double roll = random.NextDouble() * total;
                int pick = pool.Count - 1;
                for (int i = 0; i < pool.Count; i++)
                {
                    if (roll < pool[i].Weight) { pick = i; break; }
                    roll -= pool[i].Weight;
                }
                offer.Add(pool[pick].Choice);
                pool.RemoveAt(pick);
            }
            // Nothing left to learn or master: the reference's Healthstone.
            if (offer.Count == 0) offer.Add(new SurvivorsChoice(0, SurvivorsChoiceKind.Heal));
            Offer = offer;
            return offer;
        }
    }

    IEnumerable<SurvivorsChoice> SupportCandidates(int supportCatalog, Func<int, int, bool>? linkable)
    {
        foreach (int skill in _skills.Keys.OrderBy(k => k))
        {
            var taken = _supports.GetValueOrDefault(skill) ?? [];
            if (taken.Count >= SurvivorsRules.MaxSupportsPerSkill) continue;
            for (int support = 0; support < supportCatalog; support++)
                if (!taken.Contains(support) && (linkable is null || linkable(skill, support)))
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
                case SurvivorsChoiceKind.Heal:
                    // The Gateway restores the character's life; the run only spends the choice.
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
