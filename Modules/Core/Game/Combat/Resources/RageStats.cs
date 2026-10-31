using GameServer.Core.World;


/// <summary>Current (3.25+) Rage inputs and derived native stats, independent of any class selection.</summary>
static class RageStats
{
    public static RageState.Inputs Inputs(Func<string, int> get) => new(
        MaximumBonus: get("maximum_rage"), HalfMaximum: get("maximum_rage_is_halved") != 0,
        EffectPercent: get("rage_effect_+%"), LossDelayMilliseconds: get("rage_loss_delay_ms"),
        LossDelayRecoveryPercent: get("rage_loss_delay_recovery_rate_+%"),
        NoInherentLoss: get("no_inherent_rage_loss") != 0 || get("should_not_lose_rage") != 0,
        CannotGain: get("cannot_gain_rage") != 0,
        CannotGainDuringSoulGainPrevention: get("cannot_gain_rage_during_soul_gain_prevention") != 0,
        MaximumOnHitGainChance: get("gain_max_rage_on_rage_gain_from_hit_%_chance"),
        GainOnHit: get("gain_x_rage_on_hit"), GainOnAttackHit: get("gain_x_rage_on_attack_hit"),
        GainOnMeleeHit: get("gain_x_rage_on_melee_hit"), GainOnAttackCritical: get("gain_x_rage_on_attack_crit"),
        GainOnMeleeCritical: get("gain_x_rage_on_melee_crit"), GainWhenHit: get("gain_x_rage_when_hit"),
        LossRatePercent: get("rage_decay_speed_+%"));

    // Preserve fractional ranges, but multiply before dividing: 100 * (1 + 15 / 100.0) can fall below 115
    // and the native panel then floors it to 114. Promote before adding, including saturated effective Rage.
    public static DamagePacket ApplyAttack(DamagePacket hit, Func<string, int> get)
    {
        int percent = get("attack_damage_+%_final_from_rage");
        if (percent == 0) return hit;
        double numerator = Math.Max(0, 100.0 + percent);
        if (numerator == 0) return DamagePacket.Empty;
        return hit.Select((_, range) => new(range.Min * numerator / 100.0, range.Max * numerator / 100.0));
    }

    public static IEnumerable<(string Name, int Value)> Display(RageState.Value value, RageState.Inputs inputs)
    {
        yield return ("current_rage", value.Current);
        yield return ("virtual_maximum_rage", value.Maximum);
        yield return ("effective_rage_for_per_rage_effects", value.Effective);
        yield return ("attack_damage_+%_final_from_rage", value.Effective);
        yield return ("is_at_maximum_rage", value.Maximum > 0 && value.Current == value.Maximum ? 1 : 0);
        yield return ("virtual_rage_loss_delay_ms", (int)Math.Min(int.MaxValue, inputs.LossDelaySeconds * 1000));
    }
}
