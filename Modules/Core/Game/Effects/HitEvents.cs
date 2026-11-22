using GameServer.Core.Protocol.Instance;

/// <summary>
/// A hit of the player's side landed on a monster: the one hit event, raised where every hit passes
/// (AilmentsOnHitLocked, and a projectile's contact with an ailment-immune monster), after the hit's damage.
/// <see cref="CombatEvent.Who"/> says who made it: the character (its own skills, brands, heralds and mirages), a
/// totem, a trap or a mine. SkillStats are the hitting skill's own stats with its supports', when the site has them.
/// </summary>
public sealed record HitLanded(bool Critical, bool Spell, bool Killed, IReadOnlyDictionary<uint, int>? SkillStats) : CombatEvent;

public static partial class Game
{
    public static bool MonsterFrozenNow(ClientState conn, SpawnedMonster m) =>
        m.Ailments.Magnitude(MonsterElementalAilments.Kind.Freeze, ProjectileWorldTime(conn)) > 0;

    /// <summary>"Your Marked enemy": a Mark this player cast is on the monster, not another player's.</summary>
    public static bool MarkedBy(ClientState conn, SpawnedMonster m)
    {
        foreach (var curse in m.Curses.All.Values)
            if (curse.Mark && curse.Caster == conn.PeerId) return true;
        return false;
    }

    // Per hit, inside the Monsters lock: nothing is built unless a rule or a subscriber listens.
    public static void HitLandedLocked(ClientState conn, SpawnedMonster m, bool critical, bool spell,
        IReadOnlyDictionary<uint, int>? skill, string who)
    {
        bool rules = EffectRules.Listens(conn, EffectRules.What.Hit);
        if (!rules && !GameEvents.Combat.Any) return;
        var hit = new HitLanded(critical, spell, m.Dead || m.Life == 0, skill) { Monster = m, Who = who };
        if (rules) EffectRules.Run(conn, hit);
        GameEvents.RaiseCombat(conn, hit);
    }
}
