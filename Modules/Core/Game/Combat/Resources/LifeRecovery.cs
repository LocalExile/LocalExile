using GameServer.Core;

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
