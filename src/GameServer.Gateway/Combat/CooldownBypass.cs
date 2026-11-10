/// <summary>
/// The charge a skill can spend to be used while it cools down: GrantedEffectsPerLevel <c>CooldownBypassType</c>
/// 1 Endurance (Vigilant Strike), 2 Frenzy (Flicker Strike, Cold Snap), 3 Power (Cold Snap of Power); 0 and 4 none.
/// <c>active_skill_cooldown_bypass_type_override_to_power_charge</c> on the skill (Flicker Strike of Power) makes it
/// a Power Charge whatever the type. The client allows the use while it holds one above its minimum
/// (<c>current_removable_*_charges</c>, <c>0x141ea4560</c>) and spends one (<c>0x141ea4290</c>); the cooldown goes
/// on as it was.
/// </summary>
static class CooldownBypass
{
    public const string PowerOverride = "active_skill_cooldown_bypass_type_override_to_power_charge";

    public static ChargeType? Charge(int bypassType, bool overrideToPower) =>
        overrideToPower ? ChargeType.Power : bypassType switch
        {
            1 => ChargeType.Endurance,
            2 => ChargeType.Frenzy,
            3 => ChargeType.Power,
            _ => null,
        };
}
