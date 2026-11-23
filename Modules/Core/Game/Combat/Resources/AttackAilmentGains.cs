public static partial class Game
{
    public const uint FrenzyOnAttackIgnite = Stat.GainFrenzyChargeIfAttackIgnites;
    public const uint EnduranceOnAttackFreeze = Stat.GainEnduranceChargeIfAttackFreezes;

    // A hit captures its source at contact. The charge queue keeps that accepted grant, rather than consulting an
    // equipment set that may have changed before the ticker drains it. Spells, secondary hits and owned actors opt out.
    public readonly record struct AttackAilmentGains(bool IgniteFrenzy, bool FreezeEndurance)
    {
        public static AttackAilmentGains Capture(IReadOnlyDictionary<uint, int> player, bool attack) => attack
            ? new(GolemStat(player, FrenzyOnAttackIgnite) > 0, GolemStat(player, EnduranceOnAttackFreeze) > 0) : default;
    }

    static void GainFromAttackAilmentLocked(Player player, bool enabled, ChargeType type, uint source)
    {
        if (enabled && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea && CurrentGameWork)
            ChargesOf(player).Enqueue(new ChargeEvent.Grant(type, 1, Stats.Name(source)));
    }
}
