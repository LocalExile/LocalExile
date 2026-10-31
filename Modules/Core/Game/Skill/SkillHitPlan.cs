using GameServer.Core.World;


enum SkillHitKind { Unsupported, Attack, Spell }

/// <summary>Routes the supported area hit by the catalog's actual skill types, never by a trial damage roll.
/// A spell without extracted hit damage has no supported hit here; auras, beams and other DoTs do not borrow a weapon.</summary>
sealed record SkillHitPlan(SkillHitKind Kind, DamagePacket SpellBase)
{
    public bool CanHit => Kind == SkillHitKind.Attack || Kind == SkillHitKind.Spell && !SpellBase.IsZero;

    public static SkillHitPlan ForSkill(SkillCatalog skills, int effect, int level, int quality = 0) =>
        Create(skills.IsAttack(effect), skills.IsSpell(effect), skills.HitDamage(effect, level, quality));

    public static SkillHitPlan Create(bool attack, bool spell, DamagePacket spellBase) => attack
        ? new(SkillHitKind.Attack, DamagePacket.Empty)
        : spell ? new(SkillHitKind.Spell, spellBase) : new(SkillHitKind.Unsupported, DamagePacket.Empty);

    public DamagePacket BuildHit(Func<DamagePacket> attack, Func<DamagePacket, DamagePacket> spell) =>
        !CanHit ? DamagePacket.Empty : Kind == SkillHitKind.Attack ? attack() : spell(SpellBase);

    public int CriticalChance(int attackChance, Func<int> spellChance) =>
        !CanHit ? 0 : Kind == SkillHitKind.Attack ? attackChance : spellChance();
}
