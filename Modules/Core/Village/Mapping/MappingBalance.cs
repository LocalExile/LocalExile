using System.Text.Json;

namespace GameServer.Modules.Village.Mapping;

/// <summary>
/// The numbers the mapping line runs on: the client's own tier table and the three formulas the map-runners panel
/// shows (expected completion, expected time, and the chance an Atlas Runner dies in the map).
/// The formulas are the design's calibration (Agents/docs/game/kingsmarch-mapping.md §3).
/// </summary>
public static class MappingBalance
{
    /// <summary>How many maps one device may hold in its queue.</summary>
    public const int MaxQueue = 12;

    /// <summary>How many workers one device may take: the client's <c>VillageBaseStats</c> row 20726.</summary>
    public const int MaxWorkers = 6;

    /// <summary>The highest map tier there is.</summary>
    public const int MaxTier = 17;

    /// <summary>How many people the town may house: the client's <c>VillageBaseStats</c> row 20728.</summary>
    public const int PopulationLimit = 90;

    /// <summary>What the ransom of a kidnapped runner costs, in gold (the client's <c>VillageSharedConstants.RansomCost</c>).</summary>
    public const double RansomCost = 50000;

    /// <summary>What a reroll of the recruit pool costs, in gold (the client's <c>VillageRecruitBalancePerTier</c>).</summary>
    public const int RecruitRerollCost = 2000;

    /// <summary>What a super reroll of the recruit pool costs, as a multiple of <see cref="RecruitRerollCost"/>.</summary>
    public const int RecruitSuperRerollMultiplier = 8;

    /// <summary>The longest a map may take: a day (1440 minutes).</summary>
    public const double MaxMinutesPerMap = 1440;

    /// <summary>The client's own table: one row per tier, with the power a tier asks for, its base time and its base
    /// death chance.</summary>
    const string PerTierFile = "data/tables/VillageMappingPerTier.json";

    // The table's rows as the client ships them, used when the table itself cannot be read.
    static readonly int[] FallbackRequired = [2000, 2059, 2117, 2173, 2227, 2660, 2726, 2790, 2852, 2912, 3600, 3738, 3872, 4350, 4730, 5100, 5880];
    static readonly int[] FallbackMinutes = [25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100, 125];
    static readonly int[] FallbackDeath = [25, 40, 55, 70, 85, 100, 115, 130, 145, 160, 175, 190, 215, 230, 245, 260, 300];

    static readonly object _lock = new();
    static int[] _required = FallbackRequired;
    static int[] _minutes = FallbackMinutes;
    static int[] _death = FallbackDeath;
    static bool _loaded;

    /// <summary>
    /// The highest tier a device of this level may run, in the client's four bands (<c>village_can_run_maps_up_to_tier</c>):
    /// level 1 runs T1-T5 (white), level 2 runs T1-T10 (white and yellow), level 3 runs T1-T16 (and red), level 4 runs
    /// T1-T17 (and purple). A level above 4 runs every tier.
    /// </summary>
    /// <param name="deviceLevel">The device's level, 1..4.</param>
    public static int TierCap(int deviceLevel) => deviceLevel switch
    {
        <= 1 => 5,
        2 => 10,
        3 => 16,
        _ => 17,
    };

    /// <summary>The power a device of this level adds to its team: level 1 is worth nothing, level 5 is worth 12%.</summary>
    /// <param name="deviceLevel">The device's level, 1..5.</param>
    public static double DevicePowerMultiplier(int deviceLevel) => 1.0 + 0.03 * (Math.Clamp(deviceLevel, 1, 5) - 1);

    /// <summary>The team power a tier asks for: the table's <c>__col1</c> in thousandths (T1 asks 2.00, T17 asks 5.88).</summary>
    /// <param name="tier">The map's tier, 1..17.</param>
    public static double RequiredPower(int tier)
    {
        Load();
        return Row(_required, tier) / 1000.0;
    }

    /// <summary>The base time a tier's maps take, in half-minute ticks per power point: 25 for T1, 125 for T17.</summary>
    /// <param name="tier">The map's tier, 1..17.</param>
    public static double BaseMinutes(int tier)
    {
        Load();
        return Row(_minutes, tier);
    }

    /// <summary>
    /// The base chance a tier kills an Atlas Runner, in percent: the table's <c>__col3</c> in hundredths of a percent
    /// (T1 is 0.25%, T17 is 3.00%). <see cref="DeathChancePercent"/> adds the rest of the risk to it.
    /// </summary>
    /// <param name="tier">The map's tier, 1..17.</param>
    public static double BaseDeathChance(int tier)
    {
        Load();
        return Row(_death, tier) / 100.0;
    }

    /// <summary>What a map's rarity costs the team: white 1.00, yellow 1.01, red 1.02, purple 1.03.</summary>
    /// <param name="rarity">The map's rarity: 0 white, 1 yellow, 2 red, 3 purple.</param>
    public static double RarityMultiplier(byte rarity) => 1.0 + 0.01 * Math.Clamp((int)rarity, 0, 3);

    /// <summary>What a map's modifiers cost the team: 1% each, and never more than five of them count.</summary>
    /// <param name="affixCount">How many modifiers the map carries.</param>
    public static double AffixMultiplier(int affixCount) => 1.0 + 0.01 * Math.Min(Math.Max(affixCount, 0), 5);

    /// <summary>
    /// The team's power: every alive runner with the Atlas Runner specialty counts <c>min(Rank, 5) / 5</c>, so six
    /// rank-5 runners are 6.00 power points. A runner's specialty rank runs up to 9 in the client, but the mapping power
    /// scale tops out at 5, so a rank above 5 gives no extra power. A runner without the specialty (rank 0) counts for
    /// nothing.
    /// </summary>
    /// <param name="runners">The runners assigned to the device.</param>
    public static double TeamPower(IEnumerable<MappingRunner> runners)
    {
        if (runners is null) return 0;
        double power = 0;
        foreach (var runner in runners)
        {
            if (runner is null || runner.Dead || runner.Rank <= 0) continue;
            power += Math.Min(runner.Rank, 5) / 5.0;
        }
        return power;
    }

    /// <summary>
    /// The team's skill relief, in units: every alive runner with the specialty adds <c>Rank - 1</c>. The death chance
    /// turns these units into relief of 0.05% each, up to 1.5%.
    /// </summary>
    /// <param name="runners">The runners assigned to the device.</param>
    public static int SkillReliefUnits(IEnumerable<MappingRunner> runners)
    {
        if (runners is null) return 0;
        int units = 0;
        foreach (var runner in runners)
        {
            if (runner is null || runner.Dead || runner.Rank <= 0) continue;
            units += runner.Rank - 1;
        }
        return units;
    }

    /// <summary>
    /// How much of a map the team is expected to clear, in percent: the team's effective power against what the tier,
    /// the rarity and the modifiers ask for, clamped to 0..100. A team with no power clears nothing.
    /// </summary>
    /// <param name="tier">The map's tier, 1..17.</param>
    /// <param name="rarity">The map's rarity: 0 white, 1 yellow, 2 red, 3 purple.</param>
    /// <param name="affixCount">How many modifiers the map carries.</param>
    /// <param name="powerEff">The team's effective power (its power times the device's multiplier).</param>
    public static int CompletionPercent(int tier, byte rarity, int affixCount, double powerEff)
    {
        if (!double.IsFinite(powerEff) || powerEff <= 0) return 0;
        double ask = RequiredPower(tier) * RarityMultiplier(rarity) * AffixMultiplier(affixCount);
        if (ask <= 0) return 0;
        double completion = 100.0 * powerEff / ask;
        if (!double.IsFinite(completion)) return 0;
        return (int)Math.Clamp(Math.Round(completion, MidpointRounding.AwayFromZero), 0, 100);
    }

    /// <summary>
    /// How long one map takes, in minutes: the tier's base time against the team's power, half a tick per point, and
    /// longer the less of the map the team clears. A team with no power gets the tier's base time, never zero and never
    /// an infinity, and no map ever takes more than a day (1440 minutes).
    /// </summary>
    /// <param name="tier">The map's tier, 1..17.</param>
    /// <param name="rarity">The map's rarity: 0 white, 1 yellow, 2 red, 3 purple.</param>
    /// <param name="affixCount">How many modifiers the map carries.</param>
    /// <param name="powerEff">The team's effective power (its power times the device's multiplier).</param>
    /// <param name="completion">How much of the map the team clears, in percent (0..100).</param>
    public static double MinutesPerMap(int tier, byte rarity, int affixCount, double powerEff, int completion)
    {
        double baseMinutes = BaseMinutes(tier);
        if (!double.IsFinite(powerEff) || powerEff <= 0) return baseMinutes;
        int done = Math.Clamp(completion, 0, 100);
        if (done <= 0) return baseMinutes;
        double minutes = (baseMinutes / powerEff) * 0.5 * (100.0 / done);
        if (!double.IsFinite(minutes) || minutes <= 0) return baseMinutes;
        return Math.Min(minutes, MaxMinutesPerMap);
    }

    /// <summary>
    /// The chance the run kills an Atlas Runner, in percent: the tier's base chance, plus what the modifiers and the
    /// unfinished map cost, plus what the missing runners cost, less what the team's skill gives back. It never falls
    /// below 1%: a run is never risk-free.
    /// </summary>
    /// <param name="tier">The map's tier, 1..17.</param>
    /// <param name="rarity">The map's rarity: 0 white, 1 yellow, 2 red, 3 purple.</param>
    /// <param name="affixCount">How many modifiers the map carries.</param>
    /// <param name="powerEff">The team's effective power (its power times the device's multiplier).</param>
    /// <param name="completion">How much of the map the team clears, in percent (0..100).</param>
    /// <param name="workerCount">How many runners are on the device.</param>
    /// <param name="reliefUnits">The team's skill relief, in units (see <see cref="SkillReliefUnits"/>).</param>
    public static double DeathChancePercent(int tier, byte rarity, int affixCount, double powerEff, int completion,
        int workerCount, int reliefUnits)
    {
        double basePercent = BaseDeathChance(tier);
        double affixPenalty = 0.10 * Math.Min(Math.Max(affixCount, 0), 5);
        int done = Math.Clamp(completion, 0, 100);
        double completionPenalty = 0.05 * (100 - done);
        double teamPenalty = 0.20 * Math.Max(0, MaxWorkers - Math.Max(0, workerCount));
        double relief = Math.Min(1.5, 0.05 * Math.Max(0, reliefUnits));
        double death = basePercent + affixPenalty + completionPenalty + teamPenalty - relief;
        if (!double.IsFinite(death)) return 1.0;
        return Math.Clamp(death, 1.0, 100.0);
    }

    /// <summary>The monster level a map's monsters are at: the tier plus 67 (T1 is 68, T17 is 84).</summary>
    /// <param name="tier">The map's tier, 1..17.</param>
    public static int MapLevel(int tier) => Math.Clamp(tier + 67, 1, 100);

    /// <summary>Reads the client's tier table once per process; a table that cannot be read leaves the built-in rows.</summary>
    static void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                using var table = JsonDocument.Parse(File.ReadAllBytes(PerTierFile));
                int[] required = (int[])FallbackRequired.Clone();
                int[] minutes = (int[])FallbackMinutes.Clone();
                int[] death = (int[])FallbackDeath.Clone();
                foreach (var row in table.RootElement.EnumerateArray())
                {
                    int id = row.GetProperty("Id").GetInt32();
                    if (id < 1 || id > MaxTier) continue;
                    required[id - 1] = row.GetProperty("__col1").GetInt32();
                    minutes[id - 1] = row.GetProperty("__col2").GetInt32();
                    death[id - 1] = row.GetProperty("__col3").GetInt32();
                }
                _required = required;
                _minutes = minutes;
                _death = death;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Console.WriteLine($"[gateway] {PerTierFile}: {ex.Message}; using the built-in tier table");
            }
        }
    }

    /// <summary>One tier's row, with the tier clamped to the table's range.</summary>
    static int Row(int[] table, int tier) => table[Math.Clamp(tier, 1, MaxTier) - 1];
}
