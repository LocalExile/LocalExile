

// Blind on monsters, from either of the player's sources: the smoke cloud of their flask (FlaskGrounds) and a passive's
// blind aura (PassiveBuffAuras). Both are the player's own, so both count as "Enemies Blinded by you". Each source keeps
// its own set and its own blinded buff on the monster, so one ending never un-blinds the other.
static partial class Game
{
    internal const string VsBlindedCriticalStat = "critical_strike_chance_+%_vs_blinded_enemies";
    internal const string FromBlindedDamageTakenStat = "damage_taken_+%_from_blinded_enemies";

    internal static bool Blinded(ClientState conn, uint monster) =>
        conn.FlaskGrounds.Blinded(monster) || conn.BlindAura.Blinded(monster);

    // A monster's accuracy (its hits on the player) or evasion (against the player's attacks): 20% less while Blinded
    // (FlaskGrounds.BlindLessPct, PoB's Blind).
    internal static int UnderBlind(ClientState conn, uint monster, int value) =>
        Blinded(conn, monster) ? FlaskGrounds.Blinded(value) : value;

    static int StatOf(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    // critical_strike_chance_+%_vs_blinded_enemies (PoB "against Blinded Enemies": ActorCondition enemy Blinded) is an
    // increase: a hit's chance against a Blinded target is recomputed from its base with it in the increased sum, never
    // the final chance scaled. Each site computes that second chance once (null without the stat) and picks it here.
    internal static int CriticalChanceOn(ClientState conn, uint target, int chance, int? vsBlinded, int? vsProfane = null) =>
        vsProfane is int profane && conn.FlaskGrounds.Profane(target) ? profane
        : vsBlinded is int blinded && Blinded(conn, target) ? blinded : chance;

    // The default attack's (and an ordinary bow's) chance against a Blinded target: the main hand's base with the pooled
    // attack increases and the vs-Blinded one, as Pools.AttackCriticalStrikeChance is built. A release keeps the
    // unprohibited chance, as the bow's ordinary one; the roll checks the prohibitions.
    internal static int? AttackCriticalChanceVsBlinded(ClientState conn, IReadOnlyDictionary<uint, int> player, bool release = false) =>
        StatOf(player, VsBlindedCriticalStat) is not (not 0 and var extra)
            ? null : AttackCriticalChanceWith(conn, player, extra, release);

    // The default attack's (and an ordinary bow's) chance against a target on the player's profane ground: +100%
    // increased critical strike chance (PoB's SelfCritChance INC for enemies on Profane Ground).
    internal static int? AttackCriticalChanceVsProfane(ClientState conn, IReadOnlyDictionary<uint, int> player, bool release = false) =>
        AttackCriticalChanceWith(conn, player, FlaskGrounds.ProfaneCritIncrease, release);

    static int? AttackCriticalChanceWith(ClientState conn, IReadOnlyDictionary<uint, int> player, long extra, bool release)
    {
        var weapon = WeaponProfileFor(conn, activeSet: true);
        return AttackBaseCriticalStrike.Chance(!weapon.Unarmed, weapon.Critical, conn.Pools.CriticalStrikeChancePct + extra,
            name => release && IsCriticalProhibition(name) ? 0 : StatOf(player, name));
    }

    // A supported gem's chance against a Blinded target (its attack or spell base, the pooled and own increases).
    internal static int? SupportedCriticalChanceVsBlinded(ClientState conn, ushort skillId, bool release) =>
        conn.Pools.VsBlindedCriticalPct is not 0 and var extra ? SupportedCriticalChanceFor(conn, skillId, release, extra) : null;

    // A supported gem's chance against a target on the player's profane ground (+100% increased critical chance).
    internal static int? SupportedCriticalChanceVsProfane(ClientState conn, ushort skillId, bool release) =>
        SupportedCriticalChanceFor(conn, skillId, release, FlaskGrounds.ProfaneCritIncrease);

    // damage_taken_+%_from_blinded_enemies (PoB DamageTaken INC, "from Blinded Enemies") for a Blinded monster's hits, where
    // it joins the other increased damage taken (shock), and the ticks of the ignite its hit inflicted (Advance's takenFrom).
    internal static int DamageTakenFromBlindedPct(ClientState conn, uint monster, IReadOnlyDictionary<uint, int> player) =>
        Blinded(conn, monster) ? StatOf(player, FromBlindedDamageTakenStat) : 0;
}
