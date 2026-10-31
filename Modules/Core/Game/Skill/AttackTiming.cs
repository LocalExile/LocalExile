

/// <summary>
/// The attack timer every attack shares (the default attack and the attack gem skills). While a button is held the client
/// keeps aiming the attack, so a use may only start a swing once the previous attack is over; the client's jitter lets
/// one arrive up to a quarter of an attack early, and the schedule stays on its cadence
/// (Agents/docs/protocol/11-skills.md §5).
/// </summary>
static class AttackTiming
{
    /// <summary>How early a use may arrive, as a fraction of the attack time.</summary>
    public const double EarlyAllowance = 0.25;

    /// <summary>Whether a use at <paramref name="now"/> may start an attack whose previous one was at <paramref name="next"/>.</summary>
    public static bool Ready(double now, double next, double seconds) => now >= next - seconds * EarlyAllowance;

    /// <summary>When the attack after this one may start: one attack time after the later of now and the previous one.</summary>
    public static double After(double now, double next, double seconds) => Math.Max(now, next) + seconds;

    /// <summary>How long before the next use may arrive a pending hit has landed at the latest (seconds): the next use
    /// starts a new action, which cancels a hit still waiting.</summary>
    public const double LandMargin = 0.05;

    /// <summary>When an attack's hit lands after its swing starts (seconds): <paramref name="contact"/> (the animation's
    /// contact keyframe, a fraction of the attack) of the attack time plus <paramref name="offset"/>, never below 0 and
    /// never after the next use may arrive (a quarter of an attack early, less <see cref="LandMargin"/>).</summary>
    public static double ContactDelay(double seconds, double contact, double offset)
    {
        double latest = Math.Max(0, seconds * (1 - EarlyAllowance) - LandMargin);
        double delay = seconds * contact + offset;
        return double.IsFinite(delay) ? Math.Clamp(delay, 0, latest) : Math.Min(seconds / 2, latest);
    }
}
