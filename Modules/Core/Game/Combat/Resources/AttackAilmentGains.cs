public static partial class Game
{
    public const string FrenzyOnAttackIgnite = "gain_frenzy_charge_if_attack_ignites";
    public const string EnduranceOnAttackFreeze = "gain_endurance_charge_if_attack_freezes";

    // A hit captures its source at contact. The charge queue keeps that accepted grant, rather than consulting an
    // equipment set that may have changed before the ticker drains it. Spells, secondary hits and owned actors opt out.
    public readonly record struct AttackAilmentGains(bool IgniteFrenzy, bool FreezeEndurance)
    {
        public static AttackAilmentGains Capture(IReadOnlyDictionary<uint, int> player, bool attack) => attack
            ? new(GolemStat(player, FrenzyOnAttackIgnite) > 0, GolemStat(player, EnduranceOnAttackFreeze) > 0) : default;
    }

    static void GainFromAttackAilmentLocked(ClientState conn, bool enabled, ChargeType type, string source)
    {
        if (enabled && conn.PlayerLife > 0 && !conn.Closed && !conn.LeavingArea && CurrentGameWork)
            ChargesOf(conn).Enqueue(new ChargeEvent.Grant(type, 1, source));
    }
}
