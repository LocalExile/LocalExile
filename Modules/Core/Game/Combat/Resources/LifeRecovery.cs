using GameServer.Core;
public static partial class Game
{
    // The equipment stat is gated on the Vitality aura, including an aura received from an ally.
    internal static void VitalityHitRecoveryLocked(Player player, double now, IReadOnlyDictionary<uint, int> stats)
    {
        int percent = stats.GetValueOrDefault(Stat.RegeneratePctLifeOver1SecondWhenHitWhileAffectedByVitality);
        if (percent <= 0 || player.PlayerLife == 0) return;
        var state = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, state)) return;
        double perSecond = player.PlayerMaxLife * (double)percent / 100
            * PanelStats.LifeRegenerationMultiplier(key => stats.GetValueOrDefault(key));
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
    public static bool NonLeechBlocked(Func<uint, int> get) =>
        get(Stat.CannotRecoverLife) != 0 || get(Stat.KeystoneVaalPact) != 0;
}
