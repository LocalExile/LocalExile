using GameServer.Core.Protocol.Instance;

/// <summary>Lifetap Support's buff and spent-Life counter. Supported skills pay Life instead of Mana; spending
/// enough Life on their upfront costs grants the Lifetap buff (BuffDefinitions row 1846), which lasts four seconds
/// (longer with the support's quality) and adds more damage and flask Life recovery while it runs.</summary>
public sealed class LifetapState
{
    /// <summary>When the buff ends (Clock seconds). A refresh moves it later; 0 means neither running nor shown.</summary>
    public double Until;
    /// <summary>Life spent on supported skills since the previous buff, compared with the skill's threshold.</summary>
    public int Spent;
    /// <summary>The buff instance allocated for the next add; 0 before one is needed.</summary>
    public ushort Buff;

    public bool ActiveAt(double now) => Until > now;

    public void Gain(double now, double seconds)
    {
        Until = Math.Max(Until, now + Math.Max(.1, seconds));
    }

    /// <summary>Death drops the buff and the counter. The presented instance stays until Presence removes it.</summary>
    public void Clear()
    {
        Until = 0;
        Spent = 0;
    }
}

/// <summary>Lifetap Support (`SupportLifetap`, GrantedEffects 11905): the data and the pure calculations. The support's
/// stat set carries `base_skill_cost_life_instead_of_mana`, the spent-Life threshold, the more-damage value while buffed
/// and the 4000 ms base duration (plus its quality duration).</summary>
public static class Lifetap
{
    /// <summary>BuffDefinitions row <c>lifetap_buff</c> (the table's 1846th row).</summary>
    public const ushort BuffRow = 1846;

    /// <summary>Lifetap grants 20% increased Life Recovery from Flasks (poedb, BuffDefinitions 1846's stat).</summary>
    public const int FlaskRecoveryPct = 20;

    public const uint CostLifeStat = Stat.BaseSkillCostLifeInsteadOfMana,
        AlternateCostLifeStat = Stat.SkillCostLifeInsteadOfMana,
        DamageStat = Stat.SupportLifetapDamagePlusPctFinalWhileBuffed,
        ThresholdStat = Stat.SupportLifetapSpentLifeThreshold,
        DurationStat = Stat.SupportBaseLifetapBuffDuration;

    const double DefaultDurationSeconds = 4;

    public static int Value(IReadOnlyDictionary<uint, int> stats, uint key) => stats.GetValueOrDefault(key);

    /// <summary>Whether these supported-skill stats convert the Mana cost to Life, with or without Lifetap.</summary>
    public static bool ConvertsCostToLife(IReadOnlyDictionary<uint, int> stats) =>
        Value(stats, CostLifeStat) != 0 || Value(stats, AlternateCostLifeStat) != 0;

    public static bool ConvertsCostToLife(IReadOnlyDictionary<uint, int> skill, IReadOnlyDictionary<uint, int> player) =>
        ConvertsCostToLife(skill) || ConvertsCostToLife(player) || Value(player, Stat.KeystoneBloodMagic) != 0;

    // A continuous Life cost keeps the fractional payment and stops before the last point of Life.
    public static uint PayOngoingLife(uint life, double cost, ref double carry, out bool exhausted)
    {
        double due = carry + cost;
        uint available = life > 0 ? life - 1 : 0;
        uint paid = (uint)Math.Min(available, Math.Floor(due));
        carry = due - paid;
        exhausted = paid == available && due > 0;
        return paid;
    }

    /// <summary>Whether these stats come from Lifetap Support: its own spent-Life threshold is the specific marker.
    /// Generic Life-cost conversion (for example Blood Magic) must pay Life without granting Lifetap's buff.</summary>
    public static bool Supported(IReadOnlyDictionary<uint, int> stats) =>
        ConvertsCostToLife(stats) && Value(stats, ThresholdStat) > 0;

    /// <summary>The whole cost, in Life, of a skill whose Mana cost is converted to Life; 0 otherwise.</summary>
    public static int LifeCost(IReadOnlyDictionary<uint, int> stats, int manaCost) =>
        ConvertsCostToLife(stats) ? Math.Max(0, manaCost) : 0;

    public static bool ActiveAt(Player player, double now) => player.Lifetap.ActiveAt(now);

    /// <summary>The flask Life recovery increase while the buff runs, 0 otherwise.</summary>
    public static int FlaskRecovery(Player player) =>
        ActiveAt(player, player.Clock.Elapsed.TotalSeconds) ? FlaskRecoveryPct : 0;

    /// <summary>
    /// One supported skill's upfront Life cost: adds it to the spent-Life counter and grants or refreshes Lifetap
    /// when the counter reaches the skill's threshold. The counter keeps the remainder, so a large cost is not lost.
    /// </summary>
    public static void Spend(Player player, IReadOnlyDictionary<uint, int> stats, int life, double now)
    {
        if (life <= 0 || !Supported(stats)) return;
        var state = player.Lifetap;
        int threshold = Value(stats, ThresholdStat);
        if (threshold <= 0) return;
        state.Spent = (int)Math.Min(int.MaxValue, (long)state.Spent + life);
        if (state.Spent < threshold) return;

        state.Spent = Math.Max(0, state.Spent - threshold);
        double seconds = DurationSeconds(stats);
        state.Gain(now, seconds);
        player.Area.Buffs.Set(player.Id, new(BuffRow, player.Id, U68: Buff.Seconds(state.Until - now)));
        Console.WriteLine($"[gateway] Lifetap: buff for {seconds:0.##} s after {life} Life spent " +
            $"(threshold {threshold}, {state.Spent} toward the next)");
    }

    /// <summary>The buff's duration: the support's 4000 ms base, plus 100 ms per 1% quality in the same stat.</summary>
    public static double DurationSeconds(IReadOnlyDictionary<uint, int> stats) =>
        Value(stats, DurationStat) is > 0 and int milliseconds ? milliseconds / 1000.0 : DefaultDurationSeconds;

    /// <summary>
    /// The buff instance the client must add or remove since the previous call. Called under the send gate and the
    /// Monsters lock, after the cost that granted it, and by the player tick that ends it.
    /// </summary>
    public static byte[]? Presentation(Player player) =>
        PresentationRecords(player) is { } records ? [.. records.SelectMany(p => p)] : null;

    // The Lifetap buff while it lasts; its countdown is set when it is gained (Spend).
    public static byte[][]? PresentationRecords(Player player)
    {
        if (!player.Lifetap.ActiveAt(player.Clock.Elapsed.TotalSeconds)) player.Area.Buffs.Clear(player.Id, BuffRow, player.Id);
        return null;
    }
}
