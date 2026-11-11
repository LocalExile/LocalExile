using GameServer.Core.World;


public enum SkillHitKind { Unsupported, Attack, Spell }

/// <summary>Routes the supported area hit by the catalog's actual skill types, never by a trial damage roll.
/// A spell without extracted hit damage has no supported hit here; auras, beams and other DoTs do not borrow a weapon.
/// <paramref name="ChargeBase"/>: a charge-spending spell's base per removable charge (Discharge), which
/// <see cref="SpellBaseWith"/> adds to <paramref name="SpellBase"/> for the charges held.</summary>
public sealed record SkillHitPlan(SkillHitKind Kind, DamagePacket SpellBase,
    IReadOnlyList<(ChargeType Type, DamagePacket PerCharge)>? ChargeBase = null)
{
    public bool CanHit => Kind == SkillHitKind.Attack || Kind == SkillHitKind.Spell && (!SpellBase.IsZero || SpendsCharges);

    /// <summary>The spell deals its damage per removable charge (Discharge): it spends them when used.</summary>
    public bool SpendsCharges => ChargeBase is { Count: > 0 };

    /// <summary>The spell's base hit with <c>current_removable_*_charges</c> from <paramref name="get"/>.</summary>
    public DamagePacket SpellBaseWith(Func<string, int> get)
    {
        var hit = SpellBase;
        if (ChargeBase is { } perCharge)
            foreach (var (type, range) in perCharge)
                if (get($"current_removable_{Charges.Name(type)}_charges") is int held and > 0) hit = hit.Add(range.Scale((double)held));
        return hit;
    }

    public static SkillHitPlan ForSkill(SkillCatalog skills, int effect, int level, int quality = 0)
    {
        var plan = Create(skills.IsAttack(effect), skills.IsSpell(effect), skills.HitDamage(effect, level, quality));
        // Only a spell without a base of its own can deal its damage per charge; the others skip the lookups.
        if (plan.Kind != SkillHitKind.Spell || !plan.SpellBase.IsZero) return plan;
        var perCharge = Charges.Types.Select(t => (Type: t, PerCharge: skills.HitDamagePerCharge(effect, level, t, quality)))
            .Where(c => !c.PerCharge.IsZero).ToList();
        return perCharge.Count > 0 ? plan with { ChargeBase = perCharge } : plan;
    }

    public static SkillHitPlan Create(bool attack, bool spell, DamagePacket spellBase) => attack
        ? new(SkillHitKind.Attack, DamagePacket.Empty)
        : spell ? new(SkillHitKind.Spell, spellBase) : new(SkillHitKind.Unsupported, DamagePacket.Empty);

    public DamagePacket BuildHit(Func<DamagePacket> attack, Func<DamagePacket, DamagePacket> spell) =>
        !CanHit ? DamagePacket.Empty : Kind == SkillHitKind.Attack ? attack() : spell(SpellBase);

    public int CriticalChance(int attackChance, Func<int> spellChance) =>
        !CanHit ? 0 : Kind == SkillHitKind.Attack ? attackChance : spellChance();
}
