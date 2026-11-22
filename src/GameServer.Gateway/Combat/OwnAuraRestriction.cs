/// <summary>The bounded native own-aura predicate; no reservation or keystone executor.</summary>
public static class OwnAuraRestriction
{
    public const uint BaseDisabled = 21148, HeraldOfDoom = 11881, Disabled = 11075;
    public const uint BaseAura = 3004, CurseAsAura = 2730, Curse = 2532, Aura = 15801, SkillDisabled = 15798;

    public static int Value(IReadOnlyDictionary<uint, int> sources) =>
        sources.GetValueOrDefault(BaseDisabled) != 0 || sources.GetValueOrDefault(HeraldOfDoom) != 0 ? 1 : 0;

    public static bool IsAura(IReadOnlyDictionary<uint, int> skill) =>
        skill.GetValueOrDefault(BaseAura) != 0
        || skill.GetValueOrDefault(CurseAsAura) != 0 && skill.GetValueOrDefault(Curse) != 0;

    public static void PublishSkill(Dictionary<uint, int> skill, int actorRestriction)
    {
        bool aura = IsAura(skill);
        skill[Aura] = aura ? 1 : 0;
        // Preserve independent disable reasons. In particular, never write zero over another restriction.
        if (aura && actorRestriction != 0) skill[SkillDisabled] = 1;
        // A support/skill overlay cannot replace the caster's final restriction, even by cancellation.
        skill[Disabled] = actorRestriction;
    }
}
