public static partial class Game
{
    const string VitalityHitLifeRecovery = "regenerate_%_life_over_1_second_when_hit_while_affected_by_vitality";

    // The equipment stat is already gated on the Vitality aura, including an aura received from an ally.
    // As for Doppelgänger Guise, a new accepted contact refreshes this one-second regeneration contribution.
    internal static void VitalityHitRecoveryLocked(ClientState conn, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.PlayerLife == 0 || Stats.Key(VitalityHitLifeRecovery) is not uint key) return;
        int percent = stats.GetValueOrDefault(key);
        if (percent <= 0) return;
        int Get(string name) => Stats.Key(name) is uint stat ? stats.GetValueOrDefault(stat) : 0;
        var state = ResourceEffectsOf(conn);
        if (!ResourceEffectsCurrent(conn, state)) return;
        double perSecond = conn.PlayerMaxLife * (double)percent / 100 * PanelStats.LifeRegenerationMultiplier(Get);
        if (perSecond > 0) state.VitalityHitRecovery.Begin(now, 1, perSecond);
    }
}

/// <summary>The tree's rules for life recovery that is not leech (issue #1102).</summary>
public static class LifeRecovery
{
    /// <summary>Whether life recovery that is not leech is blocked: <c>cannot_recover_life</c>, or Vaal Pact's "Cannot
    /// Recover Life other than from Leech". Checked where life is committed, not on the stats, so Zealot's Oath and the
    /// like still convert life recovery to Energy Shield. Leech reads <c>cannot_recover_life</c> on its own and is not
    /// touched by the keystone (poewiki /wiki/Vaal_Pact).</summary>
    public static bool NonLeechBlocked(Func<string, int> get) =>
        get("cannot_recover_life") != 0 || get("keystone_vaal_pact") != 0;
}
