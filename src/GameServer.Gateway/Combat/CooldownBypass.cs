/// <summary>
/// The charge a skill can spend to be used while it cools down: GrantedEffectsPerLevel <c>CooldownBypassType</c>
/// 1 Endurance (Vigilant Strike), 2 Frenzy (Flicker Strike, Cold Snap), 3 Power (Cold Snap of Power); 0 and 4 none.
/// <c>active_skill_cooldown_bypass_type_override_to_power_charge</c> on the skill (Flicker Strike of Power) makes it
/// a Power Charge whatever the type. The client allows the use while it holds one above its minimum
/// (<c>current_removable_*_charges</c>, <c>0x141ea4560</c>) and spends one (<c>0x141ea4290</c>); the cooldown goes
/// on as it was.
/// </summary>
public static class CooldownBypass
{
    public const string PowerOverride = "active_skill_cooldown_bypass_type_override_to_power_charge";

    /// <summary>The character's stat that makes Flicker Strike and Vigilant Strike bypass with Power Charges instead
    /// of Frenzy or Endurance Charges (a Crucible weapon passive). The client reads only <see cref="PowerOverride"/> on
    /// the skill (<c>0x141ea4290</c>), so the server sends it there.</summary>
    public const string FlickerVigilantPowerOverride = "vigilant_and_flicker_strike_active_skill_cooldown_bypass_type_override_to_power_charge";

    /// <summary>Whether <see cref="FlickerVigilantPowerOverride"/> turns this skill's bypass into a Power Charge: Flicker
    /// Strike, Vigilant Strike and their transfigured gems, whose bypass is Endurance or Frenzy (not Vaal Flicker Strike
    /// nor the variants without one).</summary>
    public static bool FlickerVigilantOverride(string? effectId, int bypassType) =>
        bypassType is 1 or 2 && effectId is not null
        && (effectId.StartsWith("FlickerStrike", StringComparison.Ordinal) || effectId.StartsWith("VigilantStrike", StringComparison.Ordinal));

    public static ChargeType? Charge(int bypassType, bool overrideToPower) =>
        overrideToPower ? ChargeType.Power : bypassType switch
        {
            1 => ChargeType.Endurance,
            2 => ChargeType.Frenzy,
            3 => ChargeType.Power,
            _ => null,
        };
}
