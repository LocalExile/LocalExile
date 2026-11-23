using GameServer.Core;

/// <summary>
/// What happens around a charge gain or loss rather than on a hit (stat descriptions, Metadata/StatDescriptions):
/// <list type="bullet">
/// <item>"Cannot gain Charges" / "Cannot gain Endurance Charges" / "Cannot gain Power Charges": a gain of the type does
/// nothing (the minimum stays);</item>
/// <item>"on reaching maximum": Tulfall's "Gain a Frenzy Charge on reaching Maximum Power Charges" and the "Lose all …
/// Charges on reaching Maximum … Charges" of Tulfall, Malachai's Loop and The Eternal Apple, for the gain that reaches
/// it;</item>
/// <item>"when you lose a … Charge" (expiry, consumption, a "lose all" stat; death is not one): Voll's Devotion's
/// "Gain an Endurance Charge when you lose a Power Charge" and Kingsguard's "Gain X Life when you lose an Endurance
/// Charge", once per charge lost.</item>
/// </list>
/// <see cref="ChargeSession"/> reads them from the stats it observes (<see cref="Observed"/>).
/// </summary>
public static class ChargeTriggers
{
    const uint FrenzyOnMaximumPower = Stat.GainFrenzyChargeOnReachingMaximumPowerCharges;
    const uint LoseAllOnMaximumPower = Stat.LoseAllPowerChargesOnReachingMaximumPowerCharges;
    const uint LoseAllOnMaximumEndurance = Stat.LoseAllEnduranceChargesWhenReachingMaximum;
    const uint EnduranceOnPowerLoss = Stat.GainEnduranceChargeOnPowerChargeExpiry;
    const uint LifeOnEnduranceLoss = Stat.GainXLifeWhenEnduranceChargeExpiresOrConsumed;
    const uint CannotGainAny = Stat.CannotGainCharges;
    /// <summary>Daresso's Defiance: "You gain Onslaught for X seconds per Endurance Charge when Hit" (Game.ChargesWhenHit).</summary>
    public const uint OnslaughtPerEnduranceWhenHit = Stat.UniqueGainOnslaughtWhenHitDurationMsPerEnduranceCharge;

    static readonly uint[][] CannotGainStats = [.. Charges.Types.Select(t =>
        new[] { Stat.Of($"cannot_gain_{Charges.Name(t)}_charges"), Stat.Of($"base_cannot_gain_{Charges.Name(t)}_charges") })];

    /// <summary>The stats the session observes for these rules.</summary>
    public static readonly IReadOnlyList<uint> Observed =
        [FrenzyOnMaximumPower, LoseAllOnMaximumPower, LoseAllOnMaximumEndurance, EnduranceOnPowerLoss, LifeOnEnduranceLoss,
            CannotGainAny, OnslaughtPerEnduranceWhenHit, .. CannotGainStats.SelectMany(s => s),
            .. Charges.Types.Select(t => Stat.Of($"count_as_having_max_{Charges.Name(t)}_charges")),
            Stat.CountAsHavingMaxEnduranceFrenzyPowerCharges];

    /// <summary>Whether the character cannot gain charges of the type.</summary>
    public static bool CannotGain(ChargeType type, Func<uint, int> get) =>
        get(CannotGainAny) != 0 || CannotGainStats[(int)type].Any(name => get(name) != 0);

    /// <summary>What the gain that brings the type to its maximum sets off, in order.</summary>
    public static IEnumerable<ChargeGains.Gain> OnMaximum(ChargeType type, Func<uint, int> get)
    {
        if (type == ChargeType.Power)
        {
            if (get(FrenzyOnMaximumPower) != 0) yield return new(ChargeType.Frenzy, 1, "gain_frenzy_charge_on_reaching_maximum_power_charges");
            if (get(LoseAllOnMaximumPower) != 0) yield return new(ChargeType.Power, ChargeGains.Gain.All, "lose_all_power_charges_on_reaching_maximum_power_charges");
        }
        if (type == ChargeType.Endurance && get(LoseAllOnMaximumEndurance) != 0)
            yield return new(ChargeType.Endurance, ChargeGains.Gain.All, "lose_all_endurance_charges_when_reaching_maximum");
    }

    /// <summary>One loss trigger: Life to recover and/or a charge gain.</summary>
    public readonly record struct Trigger(string Source, int Life, ChargeGains.Gain? Gain);

    /// <summary>What losing <paramref name="count"/> charges of the type sets off.</summary>
    public static IEnumerable<Trigger> OnLoss(ChargeType type, int count, Func<uint, int> get)
    {
        if (type == ChargeType.Power && get(EnduranceOnPowerLoss) != 0)
            yield return new("gain_endurance_charge_on_power_charge_expiry", 0, new ChargeGains.Gain(ChargeType.Endurance, count, "gain_endurance_charge_on_power_charge_expiry"));
        if (type == ChargeType.Endurance && get(LifeOnEnduranceLoss) is int life and > 0)
            yield return new("gain_x_life_when_endurance_charge_expires_or_consumed", (int)Math.Min(int.MaxValue, (long)life * count), null);
    }
}
