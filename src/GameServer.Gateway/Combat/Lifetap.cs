using GameServer.Core.Protocol.Instance;

/// <summary>Lifetap Support's buff and spent-Life counter. Supported skills pay Life instead of Mana; spending
/// enough Life on their upfront costs grants the Lifetap buff (BuffDefinitions row 1846), which lasts four seconds
/// (longer with the support's quality) and adds more damage and flask Life recovery while it runs.</summary>
sealed class LifetapState
{
    /// <summary>When the buff ends (Clock seconds). A refresh moves it later; 0 means neither running nor shown.</summary>
    public double Until;
    /// <summary>Life spent on supported skills since the previous buff, compared with the skill's threshold.</summary>
    public int Spent;
    /// <summary>The buff instance allocated for the next add; 0 before one is needed.</summary>
    public ushort Buff;
    /// <summary>The buff instance this connection actually sent; 0 while none is presented.</summary>
    public ushort PresentedBuff;
    /// <summary>A refresh reached the threshold while the buff was already shown: replace its instance with the new
    /// duration rather than leaving the client's older countdown running.</summary>
    public bool RefreshPresented;

    public bool ActiveAt(double now) => Until > now;

    public void Gain(double now, double seconds)
    {
        Until = Math.Max(Until, now + Math.Max(.1, seconds));
        if (PresentedBuff != 0) RefreshPresented = true;
    }

    /// <summary>Death drops the buff and the counter. The presented instance stays until Presence removes it.</summary>
    public void Clear()
    {
        Until = 0;
        Spent = 0;
        RefreshPresented = false;
    }
}

/// <summary>Lifetap Support (`SupportLifetap`, GrantedEffects 11905): the data and the pure calculations. The support's
/// stat set carries `base_skill_cost_life_instead_of_mana`, the spent-Life threshold, the more-damage value while buffed
/// and the 4000 ms base duration (plus its quality duration).</summary>
static class Lifetap
{
    /// <summary>BuffDefinitions row <c>lifetap_buff</c> (the table's 1846th row).</summary>
    public const ushort BuffRow = 1846;

    /// <summary>Lifetap grants 20% increased Life Recovery from Flasks (poedb, BuffDefinitions 1846's stat).</summary>
    public const int FlaskRecoveryPct = 20;

    public const string CostLifeStat = "base_skill_cost_life_instead_of_mana",
        AlternateCostLifeStat = "skill_cost_life_instead_of_mana",
        DamageStat = "support_lifetap_damage_+%_final_while_buffed",
        ThresholdStat = "support_lifetap_spent_life_threshold",
        DurationStat = "support_base_lifetap_buff_duration";

    const double DefaultDurationSeconds = 4;

    public static int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    /// <summary>Whether these supported-skill stats make the skill pay Life instead of Mana.</summary>
    public static bool Supported(IReadOnlyDictionary<uint, int> stats) =>
        Stat(stats, CostLifeStat) != 0 || Stat(stats, AlternateCostLifeStat) != 0;

    /// <summary>The whole cost, in Life, of a skill supported by Lifetap; 0 for an unsupported one.</summary>
    public static int LifeCost(IReadOnlyDictionary<uint, int> stats, int manaCost) =>
        Supported(stats) ? Math.Max(0, manaCost) : 0;

    public static bool ActiveAt(ClientState conn, double now) => conn.Lifetap.ActiveAt(now);

    /// <summary>The flask Life recovery increase while the buff runs, 0 otherwise.</summary>
    public static int FlaskRecovery(ClientState conn) =>
        ActiveAt(conn, conn.Clock.Elapsed.TotalSeconds) ? FlaskRecoveryPct : 0;

    /// <summary>
    /// One supported skill's upfront Life cost: adds it to the spent-Life counter and grants or refreshes Lifetap
    /// when the counter reaches the skill's threshold. The counter keeps the remainder, so a large cost is not lost.
    /// </summary>
    public static void Spend(ClientState conn, IReadOnlyDictionary<uint, int> stats, int life, double now)
    {
        if (life <= 0 || !Supported(stats)) return;
        var state = conn.Lifetap;
        int threshold = Math.Max(1, Stat(stats, ThresholdStat));
        state.Spent = (int)Math.Min(int.MaxValue, (long)state.Spent + life);
        if (state.Spent < threshold) return;

        state.Spent = Math.Max(0, state.Spent - threshold);
        double seconds = DurationSeconds(stats);
        state.Gain(now, seconds);
        Console.WriteLine($"[gateway] Lifetap: buff for {seconds:0.##} s after {life} Life spent " +
            $"(threshold {threshold}, {state.Spent} toward the next)");
    }

    /// <summary>The buff's duration: the support's 4000 ms base, plus 100 ms per 1% quality in the same stat.</summary>
    public static double DurationSeconds(IReadOnlyDictionary<uint, int> stats) =>
        Stat(stats, DurationStat) is > 0 and int milliseconds ? milliseconds / 1000.0 : DefaultDurationSeconds;

    /// <summary>
    /// The buff instance the client must add or remove since the previous call. Called under the send gate and the
    /// Monsters lock, after the cost that granted it, and by the player tick that ends it.
    /// </summary>
    public static byte[]? Presentation(ClientState conn)
    {
        var state = conn.Lifetap;
        double now = conn.Clock.Elapsed.TotalSeconds;
        bool active = state.ActiveAt(now);
        var packets = new List<byte[]>();
        if (state.PresentedBuff != 0 && (!active || state.RefreshPresented))
        {
            packets.Add(InstanceEntities.RemoveBuff(Program.PlayerId, state.PresentedBuff));
            state.PresentedBuff = 0;
        }
        state.RefreshPresented = false;
        if (active && state.PresentedBuff == 0)
        {
            state.Buff = conn.NextBuff++;
            packets.Add(InstanceEntities.AddBuff(Program.PlayerId, state.Buff, BuffRow, Program.PlayerId,
                u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.Until - now))));
            state.PresentedBuff = state.Buff;
        }
        return packets.Count == 0 ? null : [.. packets.SelectMany(packet => packet)];
    }
}
