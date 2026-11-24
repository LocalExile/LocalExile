using GameServer.Core.Protocol.Instance;

/// <summary>
/// A hit of the player's side landed on a monster: the one hit event, raised where every hit passes
/// (AilmentsOnHitLocked, and a projectile's contact with an ailment-immune monster), after the hit's damage.
/// <see cref="CombatEvent.Who"/> says who made it: the character (its own skills, brands, heralds and mirages), a
/// totem, a trap or a mine. SkillStats are the hitting skill's own stats with its supports', when the site has them.
/// </summary>
public sealed record HitLanded(bool Critical, bool Spell, bool Killed, IReadOnlyDictionary<uint, int>? SkillStats) : CombatEvent
{
    /// <summary>The hitting skill's id among the character's skills; 0 for the default attack or a site that has none.</summary>
    public ushort Skill { get; init; }
    /// <summary>When the hit landed, on the clock its site works by (what a debuff it causes is timed from); null
    /// where the site gave none.</summary>
    public double? At { get; init; }
}

public static partial class Game
{
    // How deep in triggered effects the work running now is: set around the release of a triggered skill, and it
    // follows that work through its awaits, so the events it causes carry it (CombatEvent.Depth).
    static readonly AsyncLocal<int> TriggerDepthNow = new();
    public static int TriggerDepth => TriggerDepthNow.Value;

    sealed class TriggerDepthScope(int before) : IDisposable { public void Dispose() => TriggerDepthNow.Value = before; }
    /// <summary>Marks the work from here to the dispose as caused by a triggered effect at this depth.</summary>
    public static IDisposable EnterTriggered(int depth)
    {
        int before = TriggerDepthNow.Value;
        TriggerDepthNow.Value = depth;
        return new TriggerDepthScope(before);
    }

    public static bool MonsterFrozenNow(Player player, SpawnedMonster m) =>
        m.Ailments.Magnitude(MonsterElementalAilments.Kind.Freeze, ProjectileWorldTime(player)) > 0;

    /// <summary>"Your Marked enemy": a Mark this player cast is on the monster, not another player's.</summary>
    public static bool MarkedBy(Player player, SpawnedMonster m)
    {
        foreach (var curse in m.Curses.All.Values)
            if (curse.Mark && curse.Caster == player.Id) return true;
        return false;
    }

    /// <summary>Whether a rule or a subscriber listens for this player's hits: what a hit site asks before it works
    /// out something only the hit event needs.</summary>
    public static bool HitListened(Player player) => GameEvents.Combat.Any || EffectRules.Listens(player, EffectRules.What.Hit);

    /// <summary>"You have a Mark": a Mark this player cast is on some monster of the area.</summary>
    public static bool HasMarkedEnemy(Player player)
    {
        foreach (var monster in player.Area.Monsters.Values)
            if (!monster.Dead && MarkedBy(player, monster)) return true;
        return false;
    }

    /// <summary>Whether a hit was a melee one: its skill's own type, or the weapon's for the default attack.</summary>
    public static bool MeleeHit(Player player, HitLanded hit)
    {
        if (hit.Spell) return false;
        if (hit.Skill != 0 && SupportedSkill(player, hit.Skill) is { Effect: >= 0 } gem) return SkillsFor(player).IsMelee(gem.Effect);
        return ActiveMainHand(player) is not { Type.Class: "Bow" or "Wand" };
    }

    /// <summary>Whether the character's weapon is a bow.</summary>
    public static bool BowWorn(Player player) => ActiveMainHand(player) is { Type.Class: "Bow" };

    // Per hit, inside the Monsters lock: nothing is built unless a rule or a subscriber listens.
    public static void HitLandedLocked(Player player, SpawnedMonster m, bool critical, bool spell,
        IReadOnlyDictionary<uint, int>? skill, string who, ushort sourceSkill = 0, double? at = null)
    {
        bool rules = EffectRules.Listens(player, EffectRules.What.Hit);
        if (!rules && !GameEvents.Combat.Any) return;
        var hit = new HitLanded(critical, spell, m.Dead || m.Life == 0, skill)
        { Monster = m, Who = who, Depth = TriggerDepth, Skill = sourceSkill, At = at };
        if (rules) EffectRules.Run(player, hit);
        GameEvents.RaiseCombat(player, hit);
    }
}
