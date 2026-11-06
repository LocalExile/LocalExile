static partial class Game
{
    /// <summary>"Gain Soul Eater during any Flask Effect": while a flask effect is on the player, enemies killed within
    /// this radius feed a soul (poedb: 60 units). Each soul gives 5% increased attack and cast speed and 1% character
    /// size; one is lost every 0.5 s after 4 s without a kill.</summary>
    internal const string SoulEaterStat = "gain_soul_eater_during_flask_effect";
    internal const int SoulEaterRadius = 60;

    static bool SoulEaterStatPresent(IReadOnlyDictionary<uint, int> stats) =>
        Stats.Key(SoulEaterStat) is uint key && stats.GetValueOrDefault(key) != 0;

    // "Any Flask Effect": a running utility flask or a permanent one from a belt's always-apply effect. Life and mana
    // flasks do not keep a timed effect of their own here; they are not part of this character's flask bar.
    static bool AnyFlaskEffect(ClientState conn) =>
        conn.UtilityFlaskEffects.Sources().Count > 0 || MagebloodSources(conn, _ => 0).Count > 0;

    internal static bool SoulEaterActive(ClientState conn, IReadOnlyDictionary<uint, int> stats) =>
        SoulEaterStatPresent(stats) && AnyFlaskEffect(conn);

    /// <summary>The buff's attack speed part, added to the final main-hand value without rebuilding the pools.</summary>
    internal static int SoulEaterAttackSpeed(ClientState conn, IReadOnlyDictionary<uint, int> stats)
    {
        if (!SoulEaterActive(conn, stats)) return 0;
        return 5 * conn.SoulEater.Stacks(conn.Clock.Elapsed.TotalSeconds);
    }

    /// <summary>The stat snapshot's part: the attack/cast speed buckets and the capped character size.</summary>
    internal static void WithSoulEater(ClientState conn, Dictionary<uint, int> stats)
    {
        bool active = SoulEaterActive(conn, stats);
        conn.SoulEater.Enabled = active;
        if (!active) { conn.SoulEater.Clear(); return; }
        int stacks = conn.SoulEater.Stacks(conn.Clock.Elapsed.TotalSeconds);
        if (stacks <= 0) return;
        void Add(string name, int amount)
        {
            if (amount != 0 && Stats.Key(name) is uint key)
                stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + amount, int.MinValue, int.MaxValue);
        }
        Add("attack_speed_+%", 5 * stacks);
        Add("cast_speed_+%", 5 * stacks);
        Add("capped_actor_scale_+%", Math.Min(stacks, 50));
    }

    /// <summary>An enemy killed at <paramref name="at"/>: one soul when the buff is active and the player is near.</summary>
    internal static void SoulEaterSoul(ClientState conn, (int X, int Y) at)
    {
        if (!conn.SoulEater.Enabled) return;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (Dist(at, conn.Walk.Position(now)) > SoulEaterRadius) return;
            conn.SoulEater.Soul(now);
        }
    }
}
