using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ChargeRulesTests
{
    [Fact]
    public void CoreChargesGainRefreshExpireAndTriggerAtTheirLimits()
    {
        // Tulfall (+1 maximum, a Frenzy Charge and then the loss of every Power Charge on reaching the maximum), Voll's
        // Devotion (an Endurance Charge per Power Charge lost), Kingsguard (Life per Endurance Charge lost) and a 100%
        // Power Charge on kill; Frenzy Charges cannot be gained.
        var stats = new Dictionary<string, int>
        {
            ["max_power_charges"] = 1, ["add_power_charge_on_kill_%_chance"] = 100,
            ["gain_frenzy_charge_on_reaching_maximum_power_charges"] = 1,
            ["lose_all_power_charges_on_reaching_maximum_power_charges"] = 1,
            ["gain_endurance_charge_on_power_charge_expiry"] = 1, ["gain_x_life_when_endurance_charge_expires_or_consumed"] = 50,
            ["cannot_gain_frenzy_charges"] = 1,
        };
        int Get(string name) => stats.GetValueOrDefault(name);
        var session = new ChargeSession();
        session.Observe(Get);
        var rng = new Random(1);
        void Kill(double at) => session.Admit(at, [new ChargeEvent.Kill(MonsterRarity.Normal)], Get, () => default, alive: true, rng);

        // Each kill gains one and starts the duration of all of them again: 10 s from the last gain.
        Kill(0); Kill(5); Kill(8);
        Assert.Equal(3, session.Effective(ChargeType.Power));
        Assert.Equal(18, session.State.ExpiresAt(ChargeType.Power), 6);
        // Reaching the maximum of 4: no Frenzy Charge (cannot gain), every Power Charge lost, one Endurance Charge each.
        Kill(9);
        Assert.Equal(0, session.Effective(ChargeType.Power));
        Assert.Equal(0, session.Effective(ChargeType.Frenzy));
        Assert.Equal(3, session.Effective(ChargeType.Endurance));   // the maximum of 3 caps the four lost
        Assert.Equal(9, session.LastLost(ChargeType.Power));
        // The Endurance Charges expire all at once 10 s later; Kingsguard's Life is 50 per charge lost.
        session.Step(19.5, Get, () => default, alive: true, moving: false, inTown: false, rng);
        Assert.Equal(0, session.Effective(ChargeType.Endurance));
        Assert.Equal(150, session.TakeLifeGained());
        // A lower maximum (Eternal Damnation) removes what is held above it; death is not a loss.
        stats.Remove("cannot_gain_frenzy_charges");
        session.Observe(Get);
        session.Admit(20, [new ChargeEvent.Grant(ChargeType.Frenzy, 2, "test")], Get, () => default, alive: true, rng);
        Assert.Equal(2, session.Effective(ChargeType.Frenzy));
        stats["maximum_endurance_frenzy_power_charges_is_0"] = 1;
        session.Observe(Get);
        Assert.Equal(0, ChargeMaxima.Of(ChargeType.Power, Get));
        session.Step(20.1, Get, () => default, alive: true, moving: false, inTown: false, rng);
        Assert.Equal(0, session.Effective(ChargeType.Frenzy));
        Assert.Equal(20.1, session.LastLost(ChargeType.Frenzy), 6);
    }

    [Fact]
    public void PerChargeStatsScaleWithTheChargesHeldOrTheMaximum()
    {
        // 5% reduced extra damage taken from critical strikes per Endurance Charge and Deathmarked's 10% Mark effect per
        // maximum Power Charge.
        var raw = new Dictionary<string, int>
        {
            ["self_critical_strike_multiplier_-%_per_endurance_charge"] = 5,
            ["mark_skills_curse_effect_+%_per_maximum_power_charge"] = 10,
        };
        // power: the Endurance and Power Charges held.
        Dictionary<string, int> Expand(int power, int maximum) => ChargeEffects.Expand(name => raw.GetValueOrDefault(name),
            t => t == ChargeType.Frenzy ? 0 : power, null, ChargeScaling.Rules,
            t => t == ChargeType.Power ? maximum : 3);
        var held = Expand(power: 3, maximum: 4);
        Assert.Equal(15, held["base_self_critical_strike_multiplier_-%"]);
        Assert.Equal(40, held["mark_skills_curse_effect_+%"]);
        var none = Expand(power: 0, maximum: 4);
        Assert.Equal(40, none["mark_skills_curse_effect_+%"]);
        Assert.False(none.ContainsKey("base_self_critical_strike_multiplier_-%"));
        // Badge of the Brotherhood: the Frenzy maximum is the Power one; with Masterful Form the Endurance one follows.
        var badge = new Dictionary<string, int>
        {
            ["max_power_charges"] = 2, ["maximum_frenzy_charges_is_equal_to_maximum_power_charges"] = 1,
            [ChargePanelStats.EnduranceEqualsFrenzy] = 1,
        };
        Assert.Equal(new[] { 5, 5, 5 }, ChargeMaxima.Compute(name => badge.GetValueOrDefault(name)).Select(r => r.Value));
        // Ralakesh's Impatience counts as the maximum; Endurance Charge on Melee Stun's flag is the stunning skill's own.
        Assert.True(ChargeScaling.CountsAsMaximum(ChargeType.Power, n => n == "count_as_having_max_power_charges" ? 1 : 0));
        Func<string, int> support = n => n == "gain_endurance_charge_on_melee_stun" ? 1 : 0;
        Assert.Equal(ChargeType.Endurance, Assert.Single(ChargeGains.Roll(new ChargeEvent.Stun(Melee: true, support), _ => 0,
            default, new Random(1))).Type);
        Assert.Empty(ChargeGains.Roll(new ChargeEvent.Stun(Melee: false, support), _ => 0, default, new Random(1)));
    }
}
