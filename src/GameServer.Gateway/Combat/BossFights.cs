/// <summary>
/// Bosses that fight in stages. Hillock (a1q1, The Twilight Strand) fights unarmed until his life falls to half, then
/// pulls the sword out of his back (his second granted effect, ZombieBossExtract: the .act's "extract" animation), holds
/// it (Transitionable → state 2: ZombieBoss.ot's on_transition hides the sword in his back and attaches
/// OneHandSwordZombieHeld.ao) and his life fills up again. The extract skill's stat set has no life value
/// (zombie_boss_extract_life_%_to_gain is not in it), so how much he regains is the server's: all of it, as in the real
/// game. Agents/docs/protocol/12-quests.md §6.
/// </summary>
static class BossFights
{
    /// <summary>At <paramref name="AtLifePct"/> of its maximum life the monster uses its granted effect
    /// <paramref name="SkillEffect"/> (GrantedEffects row) for <paramref name="CastSeconds"/>, then its Transitionable
    /// goes to <paramref name="TransitionTo"/> and its life fills up.</summary>
    public sealed record Stage(int AtLifePct, int SkillEffect, double CastSeconds, byte TransitionTo);

    static readonly Dictionary<string, Stage> Stages = new()
    {
        // GrantedEffects 2612 ZombieBossExtract (ActiveSkills zombie_boss_extract, action ZombieBossExtract): CastTime 3000.
        ["Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal"] = new(50, 2612, 3.0, 2),
    };

    /// <summary>The variety's next stage, or null.</summary>
    public static Stage? For(string varietyId) => Stages.GetValueOrDefault(varietyId);

    /// <summary>The life after a hit, and whether the hit starts the monster's stage (<paramref name="pending"/>, null
    /// once it ran): a hit that reaches the stage's threshold leaves at least 1 life, so the stage always plays.</summary>
    public static (uint Life, bool StageStarts) Damage(uint life, uint maxLife, int damage, Stage? pending)
    {
        long after = Math.Max(0, (long)life - damage);
        if (pending is not null && after * 100 <= (long)maxLife * pending.AtLifePct) return ((uint)Math.Max(1, after), true);
        return ((uint)after, false);
    }
}
