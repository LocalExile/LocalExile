/// <summary>Negative skill restrictions are execution rules. "Spells deal no damage" still permits a utility spell;
/// it is different from "Your spells are disabled". Heralds are not auras in the client's skill types.</summary>
readonly record struct SkillRestrictions(bool AurasDisabled = false, bool SpellsDisabled = false,
    bool WarcriesDisabled = false, bool NoSpellDamage = false)
{
    public static SkillRestrictions From(Func<string, int> get) => new(
        get("base_your_auras_are_disabled") != 0 || get("your_auras_are_disabled") != 0,
        get("your_spells_are_disabled") != 0, get("your_warcry_skills_are_disabled") != 0,
        get("spell_skills_deal_no_damage") != 0);

    public string? CannotUse(bool spell, bool aura, bool warcry) =>
        aura && AurasDisabled ? "Your auras are disabled"
        : spell && SpellsDisabled ? "Your spells are disabled"
        : warcry && WarcriesDisabled ? "Your warcry skills are disabled" : null;

    public bool DealsDamage(bool spell) => !spell || (!NoSpellDamage && !SpellsDisabled);
}
