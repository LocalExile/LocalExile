using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class DischargeTests
{
    [Fact]
    public void DischargeDealsAndGrowsPerRemovableCharge()
    {
        // Discharge has no flat base: its damage is per removable charge (lightning per Power, fire per Endurance, cold
        // per Frenzy), so without the per-charge base it could not hit at all.
        var skills = new SkillCatalog();
        int discharge = skills.EffectRow("Discharge"), misery = skills.EffectRow("DischargeAltX");
        var plan = SkillHitPlan.ForSkill(skills, discharge, 20);
        Assert.True(plan.CanHit && plan.SpendsCharges);
        var held = new Dictionary<string, int> { ["current_removable_power_charges"] = 2, ["current_removable_endurance_charges"] = 1 };
        var hit = plan.SpellBaseWith(name => held.GetValueOrDefault(name));
        Assert.Equal(new DamageRange(1470, 4412), hit[DamageType.Lightning]);
        Assert.Equal(new DamageRange(1176, 1765), hit[DamageType.Fire]);
        Assert.True(hit[DamageType.Cold].IsZero);
        Assert.True(plan.SpellBaseWith(_ => 0).IsZero);
        Assert.Equal(new DamageRange(269, 806),
            SkillHitPlan.ForSkill(skills, misery, 20).SpellBaseWith(n => n == "current_removable_power_charges" ? 1 : 0)[DamageType.Lightning]);
        // 15% more area per removable charge: 3 charges, 45% more area, radius 30 → floor(30 × 1.20) = 36.
        held["area_of_effect_+%_final_per_removable_power_frenzy_or_endurance_charge"] = 15;
        Assert.Equal(36, SkillArea.Radius(30, name => held.GetValueOrDefault(name), spell: true));
        Assert.Equal(30, SkillArea.Radius(30, n => n.StartsWith("area_of_effect") ? 15 : 0, spell: true));
    }

    [Fact]
    public void DischargeSpendsOnceOnlyTheChargesAboveTheMinimum()
    {
        // The session the ticker runs: 4 Power Charges with a minimum of 1, so 3 are removable.
        var session = new ChargeSession();
        int Get(string name) => name switch { "base_minimum_power_charges" => 1, "max_power_charges" => 1, _ => 0 };
        session.Observe(Get);
        session.Admit(0, [new ChargeEvent.Grant(ChargeType.Power, 3, "test")], Get, () => default, alive: true, new Random(1));
        Assert.Equal(4, session.Effective(ChargeType.Power));
        // One use shared by its repeats and cascade areas spends once, whichever sweep lands first.
        var use = new DischargeUse(new Dictionary<ChargeType, int> { [ChargeType.Power] = session.Effective(ChargeType.Power) - 1 });
        Assert.True(use.TakeSpend());
        Assert.False(use.TakeSpend());
        var step = session.Admit(1, [new ChargeEvent.Spend(ChargeType.Power, use.Total, "Discharge")], Get, () => default,
            alive: true, new Random(1));
        Assert.True(step.CountsChanged);
        Assert.Equal(1, session.Effective(ChargeType.Power));
        // Spending more than is held never goes below the minimum.
        session.Admit(2, [new ChargeEvent.Spend(ChargeType.Power, 5, "Discharge")], Get, () => default, alive: true, new Random(1));
        Assert.Equal(1, session.Effective(ChargeType.Power));
    }
}
